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
        int NewComposers,
        int NewPieces,
        int NewSubpieces,
        int TracksImported);

    /// <summary>
    /// Imports <paramref name="tracks"/> into the canon model. Mutates
    /// <paramref name="composers"/> and <paramref name="pieces"/> by appending
    /// new entries (and extending existing pieces' Subpiece trees). Returns the
    /// new albums plus counts of what was added.
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
        var resolver = new PieceReferenceIndex();
        resolver.BuildResolver(pieces);

        // Pieces created during this very import batch aren't in the resolver yet,
        // so subsequent tracks of the same work would create a second new entry
        // without this in-batch dict.
        var newlyCreatedTopPieces = new Dictionary<(string, string), CanonPiece>(
            new CaseInsensitivePairComparer());

        int newComposers = 0, newPieces = 0, newSubpieces = 0;
        var newAlbums = new List<CanonAlbum>();

        // Group tracks by album name (iTunes "Album" field). Tracks with no
        // Album are treated as individual one-track albums — lumping them
        // together as a synthetic "(Unknown album)" produced a single album row
        // with N tracks that all collided on the UNIQUE(disc, track_number)
        // constraint when iTunes had given them the same track number (the
        // common case for standalone downloads, where each track was "#1" of
        // its own implicit single-track album). The synthetic per-track key
        // (TrackId-prefixed) gives each albumless track its own bucket;
        // album.Title gets resolved to the track Name later.
        var byAlbum = tracks
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Album)
                              ? $"__standalone__:{t.TrackId}"
                              : t.Album!,
                    StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var albumGroup in byAlbum)
        {
            // Standalone (albumless) bucket: use the track's own Name as the
            // album title; non-standalone uses the iTunes Album field.
            var isStandalone = albumGroup.Key.StartsWith("__standalone__:", StringComparison.Ordinal);
            var albumTitle = isStandalone
                ? (albumGroup.First().Name ?? "(Untitled)")
                : albumGroup.Key;

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
                // tuple would fail the save and (without the SaveAlbumsAsync
                // transaction) wipe the table. If duplicates exist within this
                // disc, renumber sequentially 1..N preserving iTunes order.
                var orderedTracks = discGroup.OrderBy(t => t.TrackNumber ?? 0).ToList();
                var distinctCount = orderedTracks.Select(t => t.TrackNumber ?? 0).Distinct().Count();
                var renumber = distinctCount != orderedTracks.Count;
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

                    var parsedComposer = ItunesImportInference.ParseComposer(track.Composer);
                    if (parsedComposer is null)
                    {
                        // No composer field — leave the track uncatalogued, fall back to its raw name.
                        albumTrack.Description = track.Name;
                        disc.Tracks.Add(albumTrack);
                        continue;
                    }

                    var composer = GetOrCreateComposer(parsedComposer, composers, composerByName, ref newComposers);
                    var parsedName = ItunesImportInference.ParseTrackName(track.Name);
                    var topPiece = ResolveOrCreateTopPiece(composer, parsedName.PieceTitle,
                                                           resolver, pieces, newlyCreatedTopPieces, ref newPieces);

                    if (parsedName.SubpieceRefs.Count == 0)
                    {
                        // Whole-piece reference.
                        albumTrack.PieceRefs =
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
                        albumTrack.PieceRefs = new List<TrackPieceRef>(parsedName.SubpieceRefs.Count);
                        foreach (var subRef in parsedName.SubpieceRefs)
                        {
                            EnsureSubpiecePath(topPiece, subRef.Path, subRef.MusicNumber, ref newSubpieces);
                            albumTrack.PieceRefs.Add(new TrackPieceRef
                            {
                                Composer     = composer.Name,
                                PieceTitle   = topPiece.Title ?? parsedName.PieceTitle,
                                SubpiecePath = subRef.Path.ToList(),
                            });
                        }
                    }

                    disc.Tracks.Add(albumTrack);
                }

                album.Discs.Add(disc);
            }

            newAlbums.Add(album);
        }

        return new ImportResult(newAlbums, newComposers, newPieces, newSubpieces, tracks.Count);
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
        ref int newCount)
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
        newCount++;
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
        ref int newCount)
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
            return resolved.Value.Piece;
        }

        var fresh = new CanonPiece
        {
            Composer      = composer.Name,
            Title         = title,
            IsProvisional = true,
        };
        pieces.Add(fresh);
        newlyCreated[key] = fresh;
        newCount++;
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
        ref int newSubpieces)
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
                newSubpieces++;
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
