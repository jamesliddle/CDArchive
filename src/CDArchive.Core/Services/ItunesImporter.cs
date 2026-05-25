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
        int TracksImported);

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
        IList<CanonPiece> pieces)
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
                              resolver, newlyCreatedTopPieces, counters);

            newLooseTracks.Add(loose);
        }

        // ── Album-bound tracks ────────────────────────────────────────────────
        var byAlbum = albumInputs
            .GroupBy(t => t.Album!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var albumGroup in byAlbum)
        {
            var albumTitle = albumGroup.Key;

            var album = new CanonAlbum
            {
                Title         = albumTitle,
                IsProvisional = true,
            };

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

            // Build a per-track lookup so the inner loop can decide on overrides.
            var performersByItunesTrackId = trackArtists
                .ToDictionary(x => x.Track.TrackId, x => x.Performers);

            var byDisc = albumGroup.GroupBy(t => t.DiscNumber ?? 1).OrderBy(g => g.Key);
            foreach (var discGroup in byDisc)
            {
                var disc = new AlbumDisc { DiscNumber = discGroup.Key };

                // Defensive renumber: the album_tracks table has UNIQUE(disc_id,
                // track_number), so two iTunes tracks sharing a (disc, track#)
                // tuple would fail the save. The track editor also requires
                // TrackNumber >= 1 (validates "Track number must be a positive
                // integer"), so a standalone MP3 with no iTunes track number
                // would land as 0 and the user would be unable to re-edit it.
                // If either condition holds — duplicates within the disc OR any
                // missing / non-positive number — renumber the whole disc
                // sequentially 1..N, preserving iTunes order.
                var orderedTracks = discGroup.OrderBy(t => t.TrackNumber ?? 0).ToList();
                var rawNumbers = orderedTracks.Select(t => t.TrackNumber ?? 0).ToList();
                var anyNonPositive = rawNumbers.Any(n => n < 1);
                var distinctCount = rawNumbers.Distinct().Count();
                var renumber = anyNonPositive || distinctCount != orderedTracks.Count;
                int seq = 1;

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
                                      resolver, newlyCreatedTopPieces, counters);

                    disc.Tracks.Add(albumTrack);
                }

                album.Discs.Add(disc);
            }

            newAlbums.Add(album);
        }

        return new ImportResult(newAlbums, newLooseTracks,
                                counters.Composers, counters.Pieces, counters.Subpieces, tracks.Count);
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
    private static void PopulatePieceRefs(
        ItunesTrack source,
        AlbumTrack  target,
        IList<CanonComposer> composers,
        Dictionary<string, CanonComposer> composerByName,
        IList<CanonPiece> pieces,
        PieceReferenceIndex resolver,
        Dictionary<(string, string), CanonPiece> newlyCreatedTopPieces,
        Counters counters)
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

        var parsedName = ItunesImportInference.ParseTrackName(source.Name);
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
                EnsureSubpiecePath(topPiece, subRef.Path, subRef.MusicNumber, counters);
                target.PieceRefs.Add(new TrackPieceRef
                {
                    Composer     = composer.Name,
                    PieceTitle   = topPiece.Title ?? parsedName.PieceTitle,
                    SubpiecePath = subRef.Path.ToList(),
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
        if (resolved.HasValue &&
            string.Equals(resolved.Value.Piece.Composer, composer.Name,
                          StringComparison.OrdinalIgnoreCase))
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
    /// missing subpieces. The <paramref name="musicNumberForLeaf"/>, if provided, is
    /// applied to the leaf (only when the leaf doesn't already carry one).
    /// </summary>
    private static void EnsureSubpiecePath(
        CanonPiece root,
        IReadOnlyList<string> path,
        string? musicNumberForLeaf,
        Counters counters)
    {
        var current = root;
        for (int i = 0; i < path.Count; i++)
        {
            var segment = path[i];
            // Leaf number applies only to the final segment of the path.
            var parsedNumber = i == path.Count - 1 ? musicNumberForLeaf : null;

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
            }
            else
            {
                // Back-fill: a structured-form subpiece (Number=1, Title="") matched
                // by-number gets the parsed title written into its Title field so
                // save-time SubpieceMatch (which checks Title / DisplayTitle /
                // SubpieceDisplayTitle) can find this same subpiece for the
                // TrackPieceRef.SubpiecePath we're about to write. Without this,
                // the in-memory match here wouldn't survive the round-trip.
                if (string.IsNullOrEmpty(existing.Title) && !string.IsNullOrWhiteSpace(segment))
                    existing.Title = segment;
                // Also fill in a missing music number when iTunes provided one.
                if (i == path.Count - 1 &&
                    !string.IsNullOrEmpty(parsedNumber) &&
                    string.IsNullOrEmpty(existing.MusicNumber))
                    existing.MusicNumber = parsedNumber;
            }

            current = existing;
        }
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
