using System.Text.RegularExpressions;
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
        IReadOnlyDictionary<int, ItunesImportInference.DotSeparatorInterpretation>? dotInterpretations = null,
        EnrichmentChoices? enrichment = null,
        IReadOnlyCollection<string>? forms = null)
    {
        // Slice 4 of the MB integration: the optional enrichment snapshot is
        // the user's review-pane decisions, plumbed through to create-time on
        // composers / albums / pieces. null preserves the legacy non-MB path
        // verbatim — the apply rules silently no-op when no choice is found.
        enrichment ??= EnrichmentChoices.Empty;

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
                // Default to DDD-stereo digital; matches the new-album +
                // editor-new-track defaults so freshly-imported content lands
                // in a consistent state.
                SparsCode     = "DDD",
                IsStereo      = true,
            };

            // Performers come straight from the track's own Artist field —
            // there's no album-level common-set deduplication to do.
            var performers = ParsePerformers(t.Artist);
            if (performers.Count > 0)
                loose.Performers = performers.Select(name => new AlbumPerformer { Name = name }).ToList();

            PopulatePieceRefs(t, loose, composers, composerByName, pieces,
                              resolver, newlyCreatedTopPieces, counters,
                              LookupDotInterpretation(dotInterpretations, t.TrackId),
                              enrichment, forms);

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
            bool isExistingProvisionalAlbum = false;
            var dedupKey = TryBuildAlbumDedupKey(albumTitle);
            if (dedupKey is { } k && existingAlbumByKey.TryGetValue(k, out var match))
            {
                album = match;
                isExistingAlbum = true;
                isExistingProvisionalAlbum = match.IsProvisional;
                modifiedAlbums++;
            }
            else
            {
                album = new CanonAlbum
                {
                    Title         = albumTitle,
                    IsProvisional = true,
                    // Sensible defaults for a freshly-imported album: DDD-
                    // stereo digital — the dominant convention for the user's
                    // modern-era acquisitions. The user can override per-album
                    // (and the editor's propagator will push the new value to
                    // every track on save).
                    SparsCode     = "DDD",
                    IsStereo      = true,
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

            // Apply rule: album enrichment lands when creating fresh OR when
            // merging into a still-provisional canon album. Approved canon
            // albums are never overwritten — the user already curated them.
            // The lookup key matches the planner's BuildAlbumKey shape, so
            // a planner proposal and an applied choice round-trip cleanly.
            if (!isExistingAlbum || isExistingProvisionalAlbum)
            {
                var firstTrack = albumGroup.First();
                var albumKey = ItunesImportEnrichmentPlanner.BuildAlbumKey(
                    albumTitle, firstTrack.AlbumArtist);
                if (enrichment.AlbumsByKey.TryGetValue(albumKey, out var albumChoice))
                {
                    ApplyAlbumChoiceToAlbum(album, albumChoice);
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
                // Sort nulls LAST (?? int.MaxValue), not first. A track with no
                // iTunes TrackNumber — common for hand-added imports, e.g. the
                // "Clari - Home! Sweet Home!" arrangement on "Sutherland An
                // Evening to Remember" — used to sort to position 0 because the
                // old `?? 0` collapsed null to 0. The disc-wide renumber below
                // then assigned it TrackNumber=1 and pushed every genuinely-
                // numbered track up by one. Sorting nulls last keeps the
                // numbered tracks in their iTunes order; the unnumbered track
                // lands at the end and gets the highest renumbered position.
                var orderedTracks = discGroup.OrderBy(t => t.TrackNumber ?? int.MaxValue).ToList();
                // rawNumbers still uses ?? 0 so anyNonPositive correctly
                // detects the missing number and triggers the renumber.
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
                        // Default to DDD-stereo digital; matches the album
                        // defaults set above and saves the user a per-track
                        // override pass post-import.
                        SparsCode     = "DDD",
                        IsStereo      = true,
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
                                      LookupDotInterpretation(dotInterpretations, track.TrackId),
                                      enrichment, forms);

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
        ItunesImportInference.DotSeparatorInterpretation dotInterpretation,
        EnrichmentChoices enrichment,
        IReadOnlyCollection<string>? forms)
    {
        var parsedComposer = ItunesImportInference.ParseComposer(source.Composer);
        if (parsedComposer is null)
        {
            // No composer field — leave the track uncatalogued, fall back to its raw name.
            target.Description = source.Name;
            return;
        }

        var composer = GetOrCreateComposer(parsedComposer, composers, composerByName, counters, enrichment);

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
                    contribParsed, composers, composerByName, counters, enrichment);
                contributorComposers.Add((contribComposer, c.Role));
            }
        }

        var parsedName = ItunesImportInference.ParseTrackName(source.Name, dotInterpretation, forms);

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
                                               contributorComposers, counters, enrichment);

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
            // Tracks the compound base numbers whose head ref we've already
            // emitted for THIS track. When a later part of the same compound
            // sits in the same track (the combined-track case), it doesn't get
            // its own ref — the head ref already covers it. A part arriving on
            // its own track (the separate-track case) has no head ref here, so
            // it emits a ref pinned to its Section marker.
            var emittedCompoundHeads = new HashSet<int>();
            foreach (var subRef in parsedName.SubpieceRefs)
            {
                // EnsureSubpiecePath returns the segment path that resolves to
                // the walked subpieces — equal to the parsed path for newly
                // created subpieces, but the matched node's own identifier for
                // existing ones (so we don't corrupt curated titles).
                var refPath = EnsureSubpiecePath(
                    topPiece, subRef.Path, subRef.MusicNumber, subRef.Tempos,
                    subRef.TemposFromFormCollapse, subRef.LeafForm, counters,
                    out var compound);

                if (compound is { IsHead: false } nonHead)
                {
                    // Later compound part. Skip when its head ref is already on
                    // this track (combined track); otherwise emit a marker-pinned
                    // ref (this part is on its own track).
                    if (emittedCompoundHeads.Contains(nonHead.BaseNumber))
                        continue;
                    target.PieceRefs.Add(new TrackPieceRef
                    {
                        Composer     = composer.Name,
                        PieceTitle   = topPiece.Title ?? parsedName.PieceTitle,
                        SubpiecePath = refPath,
                        StartMarker  = nonHead.Marker,
                    });
                    continue;
                }

                if (compound is { IsHead: true } headInfo)
                    emittedCompoundHeads.Add(headInfo.BaseNumber);

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
        Counters counters,
        EnrichmentChoices enrichment)
    {
        // Look up the user's artist enrichment choice (if any) once — used
        // for both the fresh-create and the back-fill-on-provisional paths.
        AppliedArtistEnrichment? artistChoice = null;
        if (enrichment.ArtistsByName.TryGetValue(parsed.Name, out var c) && c.Apply)
            artistChoice = c;

        if (byName.TryGetValue(parsed.Name, out var existing))
        {
            // Apply rule: only when the existing row is provisional with a
            // blank BirthDate. Approved canon is never overwritten — the
            // user's curation wins (the planner won't even propose, but the
            // apply-time guard is the defensive backstop in case planner +
            // importer drift apart).
            if (artistChoice is not null
                && existing.IsProvisional
                && string.IsNullOrEmpty(existing.BirthDate))
            {
                ApplyArtistChoiceToComposer(existing, artistChoice.Candidate);
            }
            return existing;
        }

        var fresh = new CanonComposer
        {
            Name          = parsed.Name,
            SortName      = parsed.Name,
            BirthDate     = parsed.BirthYear?.ToString(),
            DeathDate     = parsed.DeathYear?.ToString(),
            IsProvisional = true,
        };

        // Fresh composer + artist choice → apply MB's fields. iTunes-derived
        // parsed years (from "(YYYY-YYYY)" in the composer string) win when
        // present; MB fills the gaps. Same shape as
        // ApplyArtistChoiceToComposer's blank-field guard so the two paths
        // stay consistent.
        if (artistChoice is not null)
            ApplyArtistChoiceToComposer(fresh, artistChoice.Candidate);

        composers.Add(fresh);
        byName[fresh.Name] = fresh;
        counters.Composers++;
        return fresh;
    }

    /// <summary>
    /// Defensive: only fills blank fields. So if the iTunes composer field
    /// parsed years, those win; MB only contributes when the field is empty.
    /// SortName is overwritten when the existing row had the placeholder
    /// (= Name) and MB's SortName differs — that's MB-canonical surname-first
    /// form, an upgrade for the user.
    /// </summary>
    private static void ApplyArtistChoiceToComposer(CanonComposer target, MbArtistSuggestion candidate)
    {
        if (string.IsNullOrEmpty(target.BirthDate) && candidate.BirthYear is { } b)
            target.BirthDate = b.ToString();
        if (string.IsNullOrEmpty(target.DeathDate) && candidate.DeathYear is { } d)
            target.DeathDate = d.ToString();
        if (string.IsNullOrEmpty(target.BirthPlace) && !string.IsNullOrEmpty(candidate.BirthPlace))
            target.BirthPlace = candidate.BirthPlace;
        if (string.IsNullOrEmpty(target.DeathPlace) && !string.IsNullOrEmpty(candidate.DeathPlace))
            target.DeathPlace = candidate.DeathPlace;

        // SortName upgrade: a fresh composer was constructed with
        // SortName=Name as a placeholder. When MB's SortName is non-empty
        // AND differs from Name, prefer MB's canonical form.
        if (!string.IsNullOrEmpty(candidate.SortName)
            && (string.IsNullOrEmpty(target.SortName)
                || string.Equals(target.SortName, target.Name, StringComparison.Ordinal)))
        {
            target.SortName = candidate.SortName;
        }

        // MBID — overwrite even if already set; the user explicitly picked
        // this candidate in the review pane, that's the authoritative value.
        target.MusicBrainzArtistId = candidate.MbArtistId;
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
        Counters counters,
        EnrichmentChoices enrichment)
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

        // Apply rule: only at fresh-create time, only when the user has
        // chosen a work for this (composer, parsed-title). Existing canon
        // pieces are never overwritten — the resolved-from-canon path
        // above returns early before we reach here.
        var workKey = EnrichmentChoices.BuildWorkKey(composer.Name, title);
        if (enrichment.WorksByKey.TryGetValue(workKey, out var workChoice))
        {
            ApplyWorkChoiceToPiece(fresh, workChoice, counters);
        }

        pieces.Add(fresh);
        newlyCreated[key] = fresh;
        counters.Pieces++;
        return fresh;
    }

    /// <summary>
    /// Applies the user's album-enrichment choice to an album row. Called
    /// for newly-created albums and for merges into still-provisional
    /// canon albums; approved canon albums never reach here.
    /// <para>
    /// Defensive: only fills blank scalar fields, and only when the user
    /// ticked the corresponding per-aspect checkbox (Metadata / Performers /
    /// Recording session). MBID is always stamped — the user chose this
    /// candidate explicitly, that's the authoritative value.
    /// </para>
    /// </summary>
    private static void ApplyAlbumChoiceToAlbum(CanonAlbum target, AppliedAlbumEnrichment choice)
    {
        target.MusicBrainzReleaseId = choice.Candidate.MbReleaseId;

        if (choice.ApplyMetadata)
        {
            if (string.IsNullOrEmpty(target.Label) && !string.IsNullOrEmpty(choice.Candidate.Label))
                target.Label = choice.Candidate.Label;
            if (string.IsNullOrEmpty(target.CatalogueNumber) && !string.IsNullOrEmpty(choice.Candidate.CatalogueNumber))
                target.CatalogueNumber = choice.Candidate.CatalogueNumber;
            if (string.IsNullOrEmpty(target.Barcode) && !string.IsNullOrEmpty(choice.Candidate.Barcode))
                target.Barcode = choice.Candidate.Barcode;
        }

        if (choice.ApplyRecordingSession && choice.Candidate.RecordingEvents.Count > 0)
        {
            // Use the first recording event — MB releases that aggregate
            // multiple sessions sometimes return several; the album model
            // carries one session's worth of fields and the user can edit
            // post-import. Don't overwrite values the importer / user has
            // already set.
            var evt = choice.Candidate.RecordingEvents[0];
            if (string.IsNullOrEmpty(target.SessionDates)   && !string.IsNullOrEmpty(evt.Date))    target.SessionDates   = evt.Date;
            if (string.IsNullOrEmpty(target.SessionVenue)   && !string.IsNullOrEmpty(evt.Venue))   target.SessionVenue   = evt.Venue;
            if (string.IsNullOrEmpty(target.SessionCity)    && !string.IsNullOrEmpty(evt.City))    target.SessionCity    = evt.City;
            if (string.IsNullOrEmpty(target.SessionCountry) && !string.IsNullOrEmpty(evt.Country)) target.SessionCountry = evt.Country;
            if (evt.Engineers.Count > 0 && (target.SessionEngineers is null or { Count: 0 }))
                target.SessionEngineers = evt.Engineers.ToList();
            if (evt.Producers.Count > 0 && (target.SessionProducers is null or { Count: 0 }))
                target.SessionProducers = evt.Producers.ToList();
        }

        if (choice.ApplyPerformers && choice.Candidate.Credits.Count > 0)
        {
            // MB credits carry Name + Role + Instrument; map straight onto
            // AlbumPerformer. Only when the album doesn't already have
            // performers — the iTunes Artist-field-derived performers may
            // already be there and shouldn't be clobbered.
            if (target.Performers is null or { Count: 0 })
            {
                target.Performers = choice.Candidate.Credits
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(c => new AlbumPerformer
                    {
                        Name       = c.Name,
                        Role       = c.Role,
                        Instrument = c.Instrument,
                    })
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Applies the user's work-enrichment choice to a freshly-created piece.
    /// <para>
    /// <c>ApplyScalars</c>: title is upgraded to MB's canonical form when the
    /// parsed-from-iTunes title and MB's title differ — this is where you
    /// get "Piano Sonata No. 14 in C♯ minor, Op. 27 No. 2 'Moonlight'" in
    /// place of the iTunes-abbreviated "Piano Sonata #14". KeyTonality /
    /// KeyMode fill blanks. MBID is always stamped (the user chose this
    /// candidate explicitly).
    /// </para>
    /// <para>
    /// <c>ApplyMovementList</c>: replaces the piece's <see cref="CanonPiece.Subpieces"/>
    /// list with MB's canonical movements. Subsequent per-track
    /// <c>EnsureSubpiecePath</c> calls will then resolve into the MB-titled
    /// subpieces (via title or music-number match). If the iTunes track
    /// titles disagree with MB enough that the matcher misses, those tracks
    /// fall through to "create a new subpiece" — which doubles up but isn't
    /// destructive. The review pane's movement-count-mismatch detector
    /// guards against this for the common case (auto-unchecks the box).
    /// </para>
    /// </summary>
    private static void ApplyWorkChoiceToPiece(
        CanonPiece target, AppliedWorkEnrichment choice, Counters counters)
    {
        // MBID — always stamp on the user's chosen candidate.
        target.MusicBrainzWorkId = choice.Candidate.MbWorkId;

        if (choice.ApplyScalars)
        {
            if (!string.IsNullOrWhiteSpace(choice.Candidate.Title))
                target.Title = choice.Candidate.Title;
            if (string.IsNullOrEmpty(target.KeyTonality)
                && !string.IsNullOrEmpty(choice.Candidate.KeyTonality))
                target.KeyTonality = choice.Candidate.KeyTonality;
            if (string.IsNullOrEmpty(target.KeyMode)
                && !string.IsNullOrEmpty(choice.Candidate.KeyMode))
                target.KeyMode = choice.Candidate.KeyMode;
            // Catalogue: MB may carry a "Op. 27 No. 2" string; if so, parse
            // into a CatalogInfo entry. Defer the actual parser to a future
            // slice — the current MB enricher leaves Catalogue null, so this
            // branch is dormant in slice 4.
        }

        if (choice.ApplyMovementList && choice.Candidate.Movements.Count > 0)
        {
            // Replace any subpieces (the freshly-created piece has none yet)
            // with MB's canonical movements. NumberedSubpieces=true matches
            // the dominant classical convention; tempo markers are sourced
            // from MB's per-movement Tempo field when present.
            target.NumberedSubpieces = true;
            target.SubpiecesStart    = 1;
            target.Subpieces = choice.Candidate.Movements
                .OrderBy(m => m.Number)
                .Select(m =>
                {
                    var sub = new CanonPiece
                    {
                        Title         = m.Title,
                        Number        = m.Number,
                        IsProvisional = true,
                    };
                    if (!string.IsNullOrWhiteSpace(m.Tempo))
                    {
                        sub.Markers =
                        [
                            new MusicalMarker
                            {
                                Kind  = MarkerKind.Tempo,
                                Value = m.Tempo!,
                            }
                        ];
                    }
                    counters.Subpieces++;
                    return sub;
                })
                .ToList();
        }
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
        string? leafForm,
        Counters counters,
        out CompoundLeafInfo? compound)
    {
        compound = null;
        var refSegments = new List<string>(path.Count);
        var current = root;
        for (int i = 0; i < path.Count; i++)
        {
            var segment = path[i];
            var isLeaf = i == path.Count - 1;
            // Leaf number applies only to the final segment of the path.
            var parsedNumber = isLeaf ? musicNumberForLeaf : null;

            current.Subpieces ??= new List<CanonPiece>();

            // ── Compound parts (letter-suffixed number, "7a"/"7b") ──────────
            // When the leaf number carries a letter suffix, the parts sharing a
            // base number collapse into one canon piece. The first to arrive is
            // the head; later parts append their form and contribute a Section
            // marker rather than creating their own subpiece.
            var compoundMatch = isLeaf && !string.IsNullOrEmpty(parsedNumber)
                ? CompoundNumberRegex.Match(parsedNumber!)
                : Match.Empty;
            if (compoundMatch.Success)
            {
                var baseNumber = int.Parse(compoundMatch.Groups["base"].Value);
                var head = current.Subpieces.FirstOrDefault(sp => sp.Number == baseNumber);

                // Non-head: a head with this base already exists AND it isn't
                // simply this same part re-imported (title differs).
                if (head is not null
                    && !string.Equals(head.Title, segment, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(leafForm))
                        head.Form = CombineForms(head.Form, leafForm);

                    // Add (or reuse) a Section marker holding this part's title.
                    head.Markers ??= new List<MusicalMarker>();
                    var marker = head.Markers.FirstOrDefault(m =>
                        m.Kind == MarkerKind.Section &&
                        string.Equals(m.Value, segment, StringComparison.OrdinalIgnoreCase));
                    if (marker is null)
                    {
                        marker = new MusicalMarker { Kind = MarkerKind.Section, Value = segment };
                        head.Markers.Add(marker);
                    }

                    var headId = string.IsNullOrEmpty(head.Title)
                        ? head.SubpieceDisplayTitle : head.Title;
                    refSegments.Add(headId);
                    compound = new CompoundLeafInfo(baseNumber, IsHead: false,
                        new MarkerReference { Kind = MarkerKind.Section, Value = segment });
                    current = head;
                    continue;
                }

                if (head is not null)
                {
                    // Same head part re-imported — idempotent match.
                    refSegments.Add(string.IsNullOrEmpty(head.Title)
                        ? head.SubpieceDisplayTitle : head.Title);
                    compound = new CompoundLeafInfo(baseNumber, IsHead: true, null);
                    current = head;
                    continue;
                }

                // First part for this base → create it as the head, numbered by
                // the base (the suffix is dropped from the canon Number).
                var newHead = new CanonPiece
                {
                    Title         = segment,
                    Composer      = root.Composer,
                    IsProvisional = true,
                    Number        = baseNumber,
                };
                if (!string.IsNullOrEmpty(leafForm))
                {
                    newHead.Form = leafForm;
                    if (string.Equals(segment, leafForm, StringComparison.OrdinalIgnoreCase))
                        newHead.Title = null;
                }
                current.Subpieces.Add(newHead);
                counters.Subpieces++;
                refSegments.Add(string.IsNullOrEmpty(newHead.Title) ? segment : newHead.Title);
                compound = new CompoundLeafInfo(baseNumber, IsHead: true, null);
                current = newHead;
                continue;
            }

            // Genuine multi-tempo group worth enriching as Tempo markers. The
            // FormAndTempo interpretation folds the FORM into the tempo list
            // (e.g. ["Scherzando", "Allegretto"]), so its values must NOT be
            // written as tempo markers — only honour real tempo continuations.
            var hasGenuineTempos = isLeaf && temposForLeaf is { Count: > 1 } && !temposFromFormCollapse;

            var existing = FindMatchingSubpiece(current.Subpieces, segment, parsedNumber);

            if (existing is null)
            {
                existing = new CanonPiece
                {
                    Title         = segment,
                    Composer      = root.Composer,
                    IsProvisional = true,
                };
                // A purely-numeric prefix lands in the integer Number field
                // (leading zeros stripped) so subpieces sort numerically without
                // the user having to zero-pad in iTunes. A prefix carrying a
                // letter suffix ("7a") can't be an int and keeps its raw form in
                // MusicNumber — those are the compound-signal case.
                if (!string.IsNullOrEmpty(parsedNumber))
                {
                    if (int.TryParse(parsedNumber, out var orderingNumber))
                        existing.Number = orderingNumber;
                    else
                        existing.MusicNumber = parsedNumber;
                }

                // Form-leaf classifier: the leaf led with a recognised form
                // ("Air. Every valley…"). Set Form on the created leaf so the
                // canon movement is Form=Air / Title="Every valley…". When the
                // leaf was form-only ("Part I. Sinfonia") the segment IS the
                // form, so clear the Title to avoid form-in-title duplication.
                if (isLeaf && !string.IsNullOrEmpty(leafForm))
                {
                    existing.Form = leafForm;
                    if (string.Equals(segment, leafForm, StringComparison.OrdinalIgnoreCase))
                        existing.Title = null;
                }

                current.Subpieces.Add(existing);
                counters.Subpieces++;

                if (hasGenuineTempos)
                    SetTempoMarkers(existing, temposForLeaf!);

                // The ref stores the title we just assigned — strict-matches it.
                // For a form-only leaf (Title cleared) store the form so the
                // ref still resolves against the node's identifier.
                refSegments.Add(string.IsNullOrEmpty(existing.Title) ? segment : existing.Title);
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
    /// Outcome of a compound-leaf (letter-suffixed number, e.g. "7a"/"7b")
    /// during <see cref="EnsureSubpiecePath"/>. A compound groups parts that
    /// share a base number under one canon piece: the first part ("7a") is the
    /// head (its title becomes the piece title, its form the first form); each
    /// later part ("7b") appends its form to the compound and contributes a
    /// <see cref="MarkerKind.Section"/> marker holding its own title, so that a
    /// part imported on its own track can pin to that marker.
    /// </summary>
    private readonly record struct CompoundLeafInfo(
        int BaseNumber,
        bool IsHead,
        MarkerReference? Marker);

    private static readonly Regex CompoundNumberRegex = new(
        @"^(?<base>\d+)(?<suffix>[A-Za-z]+)$", RegexOptions.Compiled);

    /// <summary>Combines two form names into a compound ("Chorus" + "Recitative"
    /// → "Chorus and Recitative"), skipping the append when the addition is
    /// already present (idempotent re-import).</summary>
    private static string CombineForms(string? existingForm, string addition)
    {
        if (string.IsNullOrWhiteSpace(existingForm)) return addition;
        var parts = existingForm.Split(" and ", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim());
        if (parts.Any(p => string.Equals(p, addition, StringComparison.OrdinalIgnoreCase)))
            return existingForm;
        return existingForm + " and " + addition;
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

        // Numeric prefixes match the integer Number field regardless of leading
        // zeros ("08" ↔ Number 8); letter-suffixed prefixes ("7a") only match a
        // string MusicNumber.
        var parsedIsNumeric = int.TryParse(parsedNumber, out var parsedInt);
        return subpieces.FirstOrDefault(sp =>
            string.IsNullOrEmpty(sp.Title) &&
            (string.Equals(sp.MusicNumber, parsedNumber, StringComparison.OrdinalIgnoreCase) ||
             (parsedIsNumeric && sp.Number.HasValue && sp.Number.Value == parsedInt)));
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
        ItunesImportInference.DotSeparatorInterpretation interpretation,
        IReadOnlyCollection<string>? forms = null)
    {
        var parsed = ItunesImportInference.ParseTrackName(track.Name, interpretation, forms);
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
