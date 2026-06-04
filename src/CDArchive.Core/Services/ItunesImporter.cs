using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Orchestrates an iTunes import: groups the selected tracks into albums/discs,
/// resolves or creates the necessary composers and pieces, and produces
/// <see cref="CanonAlbum"/> entries with <see cref="TrackPieceRef"/>s wired into
/// the (possibly newly-extended) piece tree.
///
/// <para>The composers and pieces lists are mutated in place — caller is
/// responsible for persisting them via <see cref="ICanonDataService"/>. Every
/// newly created composer, piece, subpiece, album, and track is marked
/// <c>IsProvisional = true</c>.</para>
/// </summary>
public static class ItunesImporter
{
    public record ImportResult(
        IReadOnlyList<CanonAlbum> NewAlbums,
        IReadOnlyList<AlbumTrack> NewLooseTracks,
        int NewComposers,
        int NewPieces,
        int NewSubpieces,
        int TracksImported,
        // M5: number of existing canon albums into which the importer merged
        // new tracks (rather than creating a duplicate). 0 in the all-new case.
        int ModifiedAlbums = 0);

    /// <summary>
    /// Imports <paramref name="tracks"/> into the canon model. Mutates
    /// <paramref name="composers"/> and <paramref name="pieces"/> by appending
    /// new entries (and extending existing pieces' Subpiece trees). Returns the
    /// new albums, new loose tracks (singletons with no <c>Album</c> field),
    /// plus counts of what was added.
    /// <para>
    /// Tracks with an empty <see cref="ItunesTrack.Album"/> become loose tracks
    /// instead of single-track synthetic albums. The piece-ref / composer
    /// resolution is identical to the album-bound path.
    /// </para>
    /// </summary>
    public static ImportResult Import(
        IReadOnlyList<ItunesTrack> tracks,
        IList<CanonComposer> composers,
        IList<CanonPiece> pieces,
        IList<CanonAlbum>? existingAlbums = null,
        IReadOnlyDictionary<int, ItunesImportInference.DotSeparatorInterpretation>? dotInterpretations = null)
    {
        var composerByName = new Dictionary<string, CanonComposer>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in composers)
            composerByName[c.Name] = c;

        // Existing pieces are matched via the shared PieceReferenceIndex resolver
        // so the importer agrees with the save-time resolver on which canon piece
        // a parsed title points at. Critically, the resolver indexes the computed
        // DisplayTitle / DisplayTitleShort, not just Title — that's how a
        // structured-form piece (Title="", Form="Piano Concerto", Number=21, …,
        // catalog "KV 467") is found by an iTunes parsed title like
        // "Piano Concerto #21 in C, KV 467".
        // Throwaway resolver — registerAsCurrent:false so we don't steal
        // Current from the live index. Pre-fix this swallowed the static
        // accessor for the duration of the import, leaving badge converters
        // reading an empty-hits index. See Rework H7.
        var resolver = new PieceReferenceIndex(registerAsCurrent: false);
        resolver.BuildResolver(pieces);

        // Pieces created during this very import batch aren't in the resolver yet,
        // so subsequent tracks of the same work would create a second new entry
        // without this in-batch dict.
        var newlyCreatedTopPieces = new Dictionary<(string, string), CanonPiece>(
            new CaseInsensitivePairComparer());

        // M4: counters threaded through PopulatePieceRefs / GetOrCreateComposer
        // / ResolveOrCreateTopPiece / EnsureSubpiecePath as a single bundle
        // rather than three separate `ref int` parameters. Easy to miss-
        // increment one when adding the next; one bundle means one read.
        var counters = new Counters();
        var newAlbums      = new List<CanonAlbum>();
        var newLooseTracks = new List<AlbumTrack>();
        int modifiedAlbums = 0;

        // M5: build an existing-album lookup keyed on the album title (trimmed,
        // lowercased). Re-imports of the same iTunes data then route new
        // tracks into the existing album instead of creating a parallel
        // duplicate. Title-only matches the H24 filter's key shape — both
        // sides rely on unique album naming, which is the user's workflow.
        var existingAlbumByKey = new Dictionary<string, CanonAlbum>();
        if (existingAlbums is { Count: > 0 })
        {
            foreach (var a in existingAlbums)
            {
                var key = TryBuildAlbumDedupKey(a.Title);
                if (key is { } k && !existingAlbumByKey.ContainsKey(k))
                    existingAlbumByKey[k] = a;
            }
        }

        // Partition by whether iTunes gave the track an Album. Albumless rows
        // become loose tracks (no synthetic wrapping); the rest are grouped by
        // iTunes Album for the album-build path below.
        var (looseInputs, albumInputs) = PartitionByAlbum(tracks);

        // ── Loose tracks (albumless iTunes rows) ──────────────────────────────
        foreach (var t in looseInputs)
        {
            var loose = new AlbumTrack
            {
                TrackNumber   = 0,           // sentinel for "loose"
                Duration      = string.IsNullOrEmpty(t.DurationDisplay) ? null : t.DurationDisplay,
                IsProvisional = true,
            };

            // Performers come straight from the track's own Artist field —
            // there's no album-level common-set deduplication to do.
            var performers = ParsePerformers(t.Artist);
            if (performers.Count > 0)
                loose.Performers = performers.Select(name => new AlbumPerformer { Name = name }).ToList();

            PopulatePieceRefs(t, loose, composers, composerByName, pieces,
                              resolver, newlyCreatedTopPieces, counters,
                              LookupDotInterpretation(dotInterpretations, t.TrackId));

            newLooseTracks.Add(loose);
        }

        // ── Album-bound tracks ────────────────────────────────────────────────
        var byAlbum = albumInputs
            .GroupBy(t => t.Album!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var albumGroup in byAlbum)
        {
            var albumTitle = albumGroup.Key;

            // Parse each track's Artist field. iTunes encodes the performer list
            // as a comma-separated string ("Soloist, Ensemble, Conductor"); we
            // split on commas and trim each part. Performers that appear in EVERY
            // track are promoted to album-level (where the AlbumTrack model
            // inherits them unless explicitly overridden); tracks whose performer
            // set differs from that common set get the full list re-stated as a
            // track-level Performers override (non-null override replaces the
            // album default, per the model's semantics).
            var trackArtists = albumGroup
                .Select(t => (Track: t, Performers: ParsePerformers(t.Artist)))
                .ToList();

            HashSet<string> commonPerformers;
            if (trackArtists.Count == 0)
                commonPerformers = new(StringComparer.OrdinalIgnoreCase);
            else
            {
                commonPerformers = new(trackArtists[0].Performers,
                                       StringComparer.OrdinalIgnoreCase);
                foreach (var (_, perfs) in trackArtists.Skip(1))
                    commonPerformers.IntersectWith(perfs);
            }

            // M5 dedup: candidate key is the title alone (trimmed, lowercased).
            // Matches the H24 filter's key shape — albums that survive the
            // "already imported" filter line up against the same logical key
            // here. When a matching existing album is found, MERGE new tracks
            // into it rather than creating a fresh CanonAlbum. The existing
            // album's scalar fields / Performers / Session* / IsProvisional
            // are intentionally not modified — the user's curation wins.
            CanonAlbum album;
            bool isExistingAlbum = false;
            var dedupKey = TryBuildAlbumDedupKey(albumTitle);
            if (dedupKey is { } k && existingAlbumByKey.TryGetValue(k, out var match))
            {
                album = match;
                isExistingAlbum = true;
                modifiedAlbums++;
            }
            else
            {
                album = new CanonAlbum
                {
                    Title         = albumTitle,
                    IsProvisional = true,
                };

                if (commonPerformers.Count > 0)
                {
                    // Preserve the order the common names appeared in the first track.
                    var orderedCommon = trackArtists[0].Performers
                        .Where(commonPerformers.Contains)
                        .ToList();
                    album.Performers = orderedCommon
                        .Select(name => new AlbumPerformer { Name = name })
                        .ToList();
                }
            }

            // Build a per-track lookup so the inner loop can decide on overrides.
            var performersByItunesTrackId = trackArtists
                .ToDictionary(x => x.Track.TrackId, x => x.Performers);

            var byDisc = albumGroup.GroupBy(t => t.DiscNumber ?? 1).OrderBy(g => g.Key);
            foreach (var discGroup in byDisc)
            {
                // M5: when merging into an existing album, find or create the
                // matching disc by DiscNumber rather than always appending a
                // fresh one. New-album path is unchanged (always a fresh disc).
                AlbumDisc disc;
                bool isExistingDisc = false;
                if (isExistingAlbum)
                {
                    var existingDisc = album.Discs.FirstOrDefault(d => d.DiscNumber == discGroup.Key);
                    if (existingDisc is not null)
                    {
                        disc = existingDisc;
                        isExistingDisc = true;
                    }
                    else
                    {
                        disc = new AlbumDisc { DiscNumber = discGroup.Key };
                    }
                }
                else
                {
                    disc = new AlbumDisc { DiscNumber = discGroup.Key };
                }

                // Defensive renumber: the album_tracks table has UNIQUE(disc_id,
                // track_number), so two iTunes tracks sharing a (disc, track#)
                // tuple would fail the save. The track editor also requires
                // TrackNumber >= 1 (validates "Track number must be a positive
                // integer"), so a standalone MP3 with no iTunes track number
                // would land as 0 and the user would be unable to re-edit it.
                // If either condition holds — duplicates within the disc OR any
                // missing / non-positive number — renumber the whole disc
                // sequentially 1..N, preserving iTunes order.
                // When merging into an existing disc, also include the existing
                // tracks in the collision check so we don't clobber them.
                var orderedTracks = discGroup.OrderBy(t => t.TrackNumber ?? 0).ToList();
                var rawNumbers = orderedTracks.Select(t => t.TrackNumber ?? 0).ToList();
                var anyNonPositive = rawNumbers.Any(n => n < 1);
                var existingNumbers = isExistingDisc
                    ? new HashSet<int>(disc.Tracks.Select(t => t.TrackNumber))
                    : new HashSet<int>();
                var distinctCount = rawNumbers.Distinct().Count();
                var collidesWithExisting = isExistingDisc && rawNumbers.Any(existingNumbers.Contains);
                var renumber = anyNonPositive || distinctCount != orderedTracks.Count || collidesWithExisting;
                // When renumbering inside an existing disc, start from
                // max(existing) + 1 so we never collide with curated tracks.
                int seq = isExistingDisc && disc.Tracks.Count > 0
                    ? disc.Tracks.Max(t => t.TrackNumber) + 1
                    : 1;

                foreach (var track in orderedTracks)
                {
                    var albumTrack = new AlbumTrack
                    {
                        TrackNumber   = renumber ? seq++ : (track.TrackNumber ?? 0),
                        Duration      = string.IsNullOrEmpty(track.DurationDisplay) ? null : track.DurationDisplay,
                        IsProvisional = true,
                    };

                    // Track-level performer override when this track's set differs
                    // from the album-level common set. AlbumTrack.Performers semantics:
                    // non-null replaces the album-level list entirely for this track,
                    // so we have to re-state the common names too (e.g. for the Goff
                    // flute track: Scott Goff + Seattle Symphony + Gerard Schwarz).
                    if (performersByItunesTrackId.TryGetValue(track.TrackId, out var thisPerformers))
                    {
                        var thisSet = new HashSet<string>(thisPerformers, StringComparer.OrdinalIgnoreCase);
                        if (!thisSet.SetEquals(commonPerformers))
                        {
                            albumTrack.Performers = thisPerformers
                                .Select(name => new AlbumPerformer { Name = name })
                                .ToList();
                        }
                    }

                    PopulatePieceRefs(track, albumTrack, composers, composerByName, pieces,
                                      resolver, newlyCreatedTopPieces, counters,
                                      LookupDotInterpretation(dotInterpretations, track.TrackId));

                    disc.Tracks.Add(albumTrack);
                }

                // M5: when merging into an existing disc, the tracks were
                // already appended to the existing instance — don't re-add it.
                // For a new disc (existing-album-but-new-disc, OR brand-new
                // album), add the disc to the album.
                if (!isExistingDisc)
                    album.Discs.Add(disc);
            }

            // M5: only append to NewAlbums when we built a fresh CanonAlbum.
            // Existing-album merges mutate in place; the caller's `albums`
            // list (passed as existingAlbums) already contains the instance.
            if (!isExistingAlbum)
                newAlbums.Add(album);
        }

        return new ImportResult(newAlbums, newLooseTracks,
                                counters.Composers, counters.Pieces, counters.Subpieces, tracks.Count,
                                modifiedAlbums);
    }

    /// <summary>
    /// M5: builds the dedup key for an album — trimmed, lowercased title.
    /// Same shape on both sides (iTunes-side `t.Album` and canon-side
    /// `album.Title`). Returns null when the title is empty (no anchor to
    /// dedup on; always treat as new).
    /// </summary>
    private static string? TryBuildAlbumDedupKey(string? title)
    {
        var t = (title ?? "").Trim().ToLowerInvariant();
        return t.Length == 0 ? null : t;
    }

    /// <summary>
    /// Bundle for the three "new entities created during this import" counters
    /// (composers / pieces / subpieces). Pre-fix these were threaded through
    /// the import call graph as three separate <c>ref int</c> parameters, easy
    /// to miss-increment when adding new sites. The bundle is private to
    /// <see cref="ItunesImporter"/> — no external consumers — and intentionally
    /// mutable: helper methods bump the counts in place during their work,
    /// the outer <see cref="Import"/> reads the final totals into the
    /// returned <see cref="ImportResult"/>. (M4)
    /// </summary>
    private sealed class Counters
    {
        public int Composers;
        public int Pieces;
        public int Subpieces;
    }

    /// <summary>
    /// Splits the incoming iTunes rows into albumless (loose-track candidates)
    /// and album-bound (regular album-build path). Loose tracks are returned in
    /// iTunes insertion order; album-bound preserves the original sequence too,
    /// then the caller's <c>GroupBy(Album)</c> regroups them.
    /// </summary>
    private static (List<ItunesTrack> Loose, List<ItunesTrack> AlbumBound) PartitionByAlbum(
        IReadOnlyList<ItunesTrack> tracks)
    {
        var loose      = new List<ItunesTrack>();
        var albumBound = new List<ItunesTrack>();
        foreach (var t in tracks)
        {
            if (string.IsNullOrWhiteSpace(t.Album)) loose.Add(t);
            else                                    albumBound.Add(t);
        }
        return (loose, albumBound);
    }

    /// <summary>
    /// Shared piece-ref / composer resolution for one input iTunes row. Mutates
    /// <paramref name="target"/> in place: sets <see cref="AlbumTrack.PieceRefs"/>
    /// when the row has a composer, otherwise sets <see cref="AlbumTrack.Description"/>
    /// to the raw iTunes Name. Also appends to <paramref name="composers"/> /
    /// <paramref name="pieces"/> as needed and bumps the <c>new*</c> counters.
    /// </summary>
    /// <summary>
    /// Default fallback when the user dialog hasn't supplied a per-track
    /// interpretation. SubpieceHierarchy preserves the historical (pre-dialog)
    /// behaviour for tracks the user didn't review.
    /// </summary>
    private static ItunesImportInference.DotSeparatorInterpretation LookupDotInterpretation(
        IReadOnlyDictionary<int, ItunesImportInference.DotSeparatorInterpretation>? choices,
        int trackId)
    {
        if (choices is not null && choices.TryGetValue(trackId, out var pick)) return pick;
        return ItunesImportInference.DotSeparatorInterpretation.SubpieceHierarchy;
    }

    private static void PopulatePieceRefs(
        ItunesTrack source,
        AlbumTrack  target,
        IList<CanonComposer> composers,
        Dictionary<string, CanonComposer> composerByName,
        IList<CanonPiece> pieces,
        PieceReferenceIndex resolver,
        Dictionary<(string, string), CanonPiece> newlyCreatedTopPieces,
        Counters counters,
        ItunesImportInference.DotSeparatorInterpretation dotInterpretation)
    {
        var parsedComposer = ItunesImportInference.ParseComposer(source.Composer);
        if (parsedComposer is null)
        {
            // No composer field — leave the track uncatalogued, fall back to its raw name.
            target.Description = source.Name;
            return;
        }

        var composer = GetOrCreateComposer(parsedComposer, composers, composerByName, counters);

        // Ensure each contributor (e.g. "compl. Franco Alfano") has a
        // CanonComposer entry — their works often live in the canon too.
        // Keep the resolved instances so the piece's Composers list can
        // be populated below with the canonical names.
        List<(CanonComposer Composer, string Role)>? contributorComposers = null;
        if (parsedComposer.Contributors is { Count: > 0 } contribs)
        {
            contributorComposers = new List<(CanonComposer, string)>(contribs.Count);
            foreach (var c in contribs)
            {
                var contribParsed = new ItunesImportInference.ParsedComposer(
                    c.Name, c.BirthYear, c.DeathYear);
                var contribComposer = GetOrCreateComposer(
                    contribParsed, composers, composerByName, counters);
                contributorComposers.Add((contribComposer, c.Role));
            }
        }

        var parsedName = ItunesImportInference.ParseTrackName(source.Name, dotInterpretation);

        // When ParseTrackName couldn't extract a top-level piece title (an
        // iTunes Name that doesn't follow the "Work - Movement" convention,
        // or one that starts straight with the segment separator), don't
        // fabricate a piece-ref pointing at an empty title — the resolver
        // can't route it and the Tracks view renders it as a blank cell.
        // Fall back to the no-composer behaviour: stash the raw iTunes name
        // in Description so the user has something visible to triage from.
        if (string.IsNullOrWhiteSpace(parsedName.PieceTitle))
        {
            target.Description = source.Name;
            return;
        }

        var topPiece = ResolveOrCreateTopPiece(composer, parsedName.PieceTitle,
                                               resolver, pieces, newlyCreatedTopPieces,
                                               contributorComposers, counters);

        if (parsedName.SubpieceRefs.Count == 0)
        {
            target.PieceRefs =
            [
                new TrackPieceRef
                {
                    Composer   = composer.Name,
                    PieceTitle = topPiece.Title ?? parsedName.PieceTitle,
                }
            ];
        }
        else
        {
            target.PieceRefs = new List<TrackPieceRef>(parsedName.SubpieceRefs.Count);
            foreach (var subRef in parsedName.SubpieceRefs)
            {
                // EnsureSubpiecePath returns the segment path that resolves to
                // the walked subpieces — equal to the parsed path for newly
                // created subpieces, but the matched node's own identifier for
                // existing ones (so we don't corrupt curated titles).
                var refPath = EnsureSubpiecePath(
                    topPiece, subRef.Path, subRef.MusicNumber, subRef.Tempos,
                    subRef.TemposFromFormCollapse, counters);
                target.PieceRefs.Add(new TrackPieceRef
                {
                    Composer     = composer.Name,
                    PieceTitle   = topPiece.Title ?? parsedName.PieceTitle,
                    SubpiecePath = refPath,
                });
            }
        }
    }

    /// <summary>
    /// Splits a comma-separated iTunes Artist field into trimmed, non-empty
    /// performer names. Preserves order; does NOT dedupe (caller decides).
    /// </summary>
    private static List<string> ParsePerformers(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return new();
        return artist
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static CanonComposer GetOrCreateComposer(
        ItunesImportInference.ParsedComposer parsed,
        IList<CanonComposer> composers,
        Dictionary<string, CanonComposer> byName,
        Counters counters)
    {
        if (byName.TryGetValue(parsed.Name, out var existing))
            return existing;

        var fresh = new CanonComposer
        {
            Name          = parsed.Name,
            SortName      = parsed.Name,
            BirthDate     = parsed.BirthYear?.ToString(),
            DeathDate     = parsed.DeathYear?.ToString(),
            IsProvisional = true,
        };
        composers.Add(fresh);
        byName[fresh.Name] = fresh;
        counters.Composers++;
        return fresh;
    }

    /// <summary>
    /// Finds the canon piece (title-form or structured-form) that matches the
    /// parsed <paramref name="title"/> for the given <paramref name="composer"/>,
    /// or creates a new provisional top-level piece if nothing matches.
    /// <para>
    /// Lookup order:
    /// <list type="number">
    ///   <item>In-batch dict — pieces created earlier in this same import call.</item>
    ///   <item><see cref="PieceReferenceIndex.TryResolve"/> — the canonical
    ///     fuzzy-match path, which indexes Title, DisplayTitle, DisplayTitleShort,
    ///     and stripped-nickname/subtitle variants. This is what lets a parsed
    ///     "Piano Concerto #21 in C, KV 467" find an existing piece whose Title
    ///     is empty but whose form/number/key/catalog computes to that same
    ///     display string.</item>
    ///   <item>Create a new title-form CanonPiece, mark provisional, append.</item>
    /// </list>
    /// </para>
    /// </summary>
    private static CanonPiece ResolveOrCreateTopPiece(
        CanonComposer composer, string title,
        PieceReferenceIndex resolver,
        IList<CanonPiece> pieces,
        Dictionary<(string, string), CanonPiece> newlyCreated,
        IReadOnlyList<(CanonComposer Composer, string Role)>? contributors,
        Counters counters)
    {
        var key = (composer.Name, title);
        if (newlyCreated.TryGetValue(key, out var fromBatch))
            return fromBatch;

        var probe = new TrackPieceRef { Composer = composer.Name, PieceTitle = title };
        var resolved = resolver.TryResolve(probe);
        // The resolver already keys its index by composer, so a hit ALREADY
        // matched this composer — re-checking the resolved piece's own
        // Composer is redundant and, worse, wrong for set members. Members of
        // a "set" container (e.g. Beethoven's "Three Piano Sonatas, WoO 47")
        // are registered under their parent's (inherited) composer but carry
        // a null Composer field of their own. The pre-fix re-check
        // `resolved.Piece.Composer == composer.Name` then failed against null
        // and the importer created a duplicate top-level piece for every set
        // member. Accept the hit when the resolved piece's composer is the
        // expected one OR is blank (inherited from the set parent).
        var resolvedComposer = resolved?.Piece.Composer;
        if (resolved.HasValue &&
            (string.IsNullOrWhiteSpace(resolvedComposer) ||
             string.Equals(resolvedComposer, composer.Name, StringComparison.OrdinalIgnoreCase)))
        {
            // The resolver returns the leaf piece for the probe. A no-subpath
            // probe resolves to a top-level piece (or a set member registered
            // as one) — exactly what we want here.
            //
            // Don't overwrite an existing piece's Composers list. The user has
            // already curated it; an iTunes-derived credit list shouldn't churn
            // already-approved metadata.
            return resolved.Value.Piece;
        }

        var fresh = new CanonPiece
        {
            Composer      = composer.Name,
            Title         = title,
            IsProvisional = true,
        };

        // Populate the Composers list with the contributors only. The principal
        // lives in Composer; Composers is the additional-contributors list per
        // the (de-facto) contract every consumer enforces — HasDirectContribution
        // and AddRolesFrom both filter out no-role entries, so a principal entry
        // would be dead weight that the PieceEditorWindow displays under "Other
        // Contributors" and confuses the user.
        if (contributors is { Count: > 0 })
        {
            fresh.Composers = contributors
                .Select(c => new ComposerCredit { Name = c.Composer.Name, Role = c.Role })
                .ToList();
        }

        pieces.Add(fresh);
        newlyCreated[key] = fresh;
        counters.Pieces++;
        return fresh;
    }

    /// <summary>
    /// Walks <paramref name="path"/> from <paramref name="root"/> down, creating any
    /// missing subpieces, and returns the path of segment strings to store on the
    /// album <see cref="TrackPieceRef.SubpiecePath"/> so it resolves back to the
    /// walked subpieces.
    ///
    /// <para><b>Created</b> subpieces are titled by the parsed segment, so the ref
    /// stores that segment (it strict-matches the new title). When
    /// <paramref name="temposForLeaf"/> has more than one entry the newly-created
    /// leaf also gets one numbered <see cref="MarkerKind.Tempo"/> marker per tempo
    /// (e.g. iTunes "1. Lento - Allegro agitato" → one movement, two tempo markers).</para>
    ///
    /// <para><b>Matched</b> (already-existing, often user-curated) subpieces are left
    /// COMPLETELY untouched — no title overwrite, no music-number back-fill, no marker
    /// enrichment. Pre-fix the leaf's title was overwritten with the parsed tempo
    /// string, which corrupted a structural movement like "4. Rondo" (stored as
    /// Title="", Number=4, Form="Rondo") into the ugly
    /// "Rondo - Allegro, ma non troppo - Più allegro quasi presto". To keep the ref
    /// resolvable without mutating the node, the stored segment is the matched node's
    /// own resolvable identifier: its <see cref="CanonPiece.Title"/> when it has one
    /// (strict match), else its <see cref="CanonPiece.SubpieceDisplayTitle"/> (which
    /// carries the "N. " number prefix the resolver's loose match keys on AND
    /// strict-matches the unmutated node).</para>
    /// </summary>
    private static List<string> EnsureSubpiecePath(
        CanonPiece root,
        IReadOnlyList<string> path,
        string? musicNumberForLeaf,
        IReadOnlyList<string>? temposForLeaf,
        bool temposFromFormCollapse,
        Counters counters)
    {
        var refSegments = new List<string>(path.Count);
        var current = root;
        for (int i = 0; i < path.Count; i++)
        {
            var segment = path[i];
            var isLeaf = i == path.Count - 1;
            // Leaf number applies only to the final segment of the path.
            var parsedNumber = isLeaf ? musicNumberForLeaf : null;

            // Genuine multi-tempo group worth enriching as Tempo markers. The
            // FormAndTempo interpretation folds the FORM into the tempo list
            // (e.g. ["Scherzando", "Allegretto"]), so its values must NOT be
            // written as tempo markers — only honour real tempo continuations.
            var hasGenuineTempos = isLeaf && temposForLeaf is { Count: > 1 } && !temposFromFormCollapse;

            current.Subpieces ??= new List<CanonPiece>();
            var existing = FindMatchingSubpiece(current.Subpieces, segment, parsedNumber);

            if (existing is null)
            {
                existing = new CanonPiece
                {
                    Title         = segment,
                    Composer      = root.Composer,
                    IsProvisional = true,
                };
                if (!string.IsNullOrEmpty(parsedNumber))
                    existing.MusicNumber = parsedNumber;
                current.Subpieces.Add(existing);
                counters.Subpieces++;

                if (hasGenuineTempos)
                    SetTempoMarkers(existing, temposForLeaf!);

                // The ref stores the title we just assigned — strict-matches it.
                refSegments.Add(segment);
            }
            else
            {
                // Matched an existing subpiece. Don't overwrite its curated
                // identity (title/number), but DO enrich it with the parsed
                // tempo markers when it has none — re-importing a multi-tempo
                // movement should fill in the tempos a structural canon entry
                // is missing. Skipped for the FormAndTempo collapse (would
                // plant the form as a bogus tempo) and when markers already
                // exist (never clobber curated markers).
                if (hasGenuineTempos && (existing.Markers is null || existing.Markers.Count == 0))
                    SetTempoMarkers(existing, temposForLeaf!);

                // Compute the ref segment from the node's CURRENT (post-enrich)
                // identifier so it still resolves: its Title when it has one,
                // else its SubpieceDisplayTitle (carries the "N. " prefix the
                // resolver's loose match keys on, and which now reflects any
                // markers we just added).
                refSegments.Add(string.IsNullOrEmpty(existing.Title)
                    ? existing.SubpieceDisplayTitle
                    : existing.Title);
            }

            current = existing;
        }

        return refSegments;
    }

    /// <summary>
    /// Replaces a subpiece's markers with one numbered <see cref="MarkerKind.Tempo"/>
    /// marker per parsed tempo, in order (e.g. iTunes "1. Lento - Allegro agitato"
    /// → two tempo markers on one movement).
    /// </summary>
    private static void SetTempoMarkers(CanonPiece leaf, IReadOnlyList<string> tempos)
    {
        leaf.Markers = new List<MusicalMarker>(tempos.Count);
        for (int j = 0; j < tempos.Count; j++)
            leaf.Markers.Add(new MusicalMarker
            {
                Kind   = MarkerKind.Tempo,
                Value  = tempos[j],
                Number = j + 1,
            });
    }

    /// <summary>
    /// Finds an existing subpiece that matches the parsed segment, preferring
    /// title-equality (case-insensitive) and falling back to a number-equality
    /// match against an empty-title subpiece. The number fallback is what bridges
    /// the structured-form Mozart concerto subtree (Title="", Number=1) with
    /// iTunes-style "1. Allegro" segments.
    /// </summary>
    private static CanonPiece? FindMatchingSubpiece(
        List<CanonPiece> subpieces, string segment, string? parsedNumber)
    {
        var byTitle = subpieces.FirstOrDefault(sp =>
            string.Equals(sp.Title, segment, StringComparison.OrdinalIgnoreCase));
        if (byTitle is not null) return byTitle;

        if (string.IsNullOrEmpty(parsedNumber)) return null;

        return subpieces.FirstOrDefault(sp =>
            string.IsNullOrEmpty(sp.Title) &&
            (string.Equals(sp.MusicNumber, parsedNumber, StringComparison.OrdinalIgnoreCase) ||
             (sp.Number.HasValue && sp.Number.Value.ToString() == parsedNumber)));
    }

    /// <summary>
    /// Dry-run check used by the import dialog: would importing
    /// <paramref name="track"/> under <paramref name="interpretation"/> resolve
    /// entirely against the existing canon WITHOUT creating any new top piece
    /// or subpiece?
    ///
    /// <para>Critically this mirrors what <see cref="Import"/> actually does —
    /// it walks each parsed subpiece path with the same
    /// <see cref="FindMatchingSubpiece"/> title-OR-music-number matching the
    /// real <c>EnsureSubpiecePath</c> uses. A plain
    /// <see cref="PieceReferenceIndex.TryResolve"/> check (which only matches
    /// on title/display-string) is too strict: it misses the common case where
    /// the canon stores a movement structurally (Title="", Form="Rondo",
    /// Number=4) and the iTunes name carries "4. Rondo. Allegro…" — the
    /// importer matches movement #4 by its number and creates nothing, but a
    /// string-only resolve fails and the dialog would needlessly prompt.</para>
    ///
    /// <para>Returns false when the top piece doesn't exist, when the parse
    /// yields no usable title, or when any path segment would have to be
    /// created.</para>
    /// </summary>
    public static bool ResolvesWithoutCreating(
        ItunesTrack track,
        string composerName,
        PieceReferenceIndex resolver,
        ItunesImportInference.DotSeparatorInterpretation interpretation)
    {
        var parsed = ItunesImportInference.ParseTrackName(track.Name, interpretation);
        if (string.IsNullOrWhiteSpace(parsed.PieceTitle)) return false;

        // The top piece must already exist (no-subpath probe resolves to it).
        var topResolved = resolver.TryResolve(new TrackPieceRef
        {
            Composer   = composerName,
            PieceTitle = parsed.PieceTitle,
        });
        if (topResolved is null) return false;
        var top = topResolved.Value.Piece;

        // A bare top-level reference with no subpiece path: the top exists, done.
        if (parsed.SubpieceRefs.Count == 0) return true;

        // Walk every parsed path with the importer's own matching semantics.
        // Any segment that wouldn't find an existing subpiece means the import
        // would create new structure → not a clean resolve.
        foreach (var subRef in parsed.SubpieceRefs)
        {
            var current = top;
            for (int i = 0; i < subRef.Path.Count; i++)
            {
                var isLeaf = i == subRef.Path.Count - 1;
                var num    = isLeaf ? subRef.MusicNumber : null;
                var subs   = current.Subpieces;
                var match  = subs is { Count: > 0 }
                    ? FindMatchingSubpiece(subs, subRef.Path[i], num)
                    : null;
                if (match is null) return false;
                current = match;
            }
        }
        return true;
    }

    private sealed class CaseInsensitivePairComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(
                obj.Item1?.ToLowerInvariant(),
                obj.Item2?.ToLowerInvariant());
    }
}
