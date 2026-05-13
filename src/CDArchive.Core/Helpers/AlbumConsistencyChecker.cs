using CDArchive.Core.Models;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Validates every <see cref="TrackPieceRef"/> in a collection of
/// <see cref="CanonAlbum"/>s against the live Canon piece list and returns
/// a report of any references that can no longer be resolved.
/// </summary>
public static class AlbumConsistencyChecker
{
    /// <summary>
    /// Walks all albums and attempts to resolve each <see cref="TrackPieceRef"/>
    /// against <paramref name="allPieces"/>.  Returns one <see cref="BrokenRef"/>
    /// per unresolvable reference.  An empty sequence means all references are valid.
    /// </summary>
    public static IReadOnlyList<BrokenRef> FindBrokenRefs(
        IEnumerable<CanonAlbum> albums,
        IEnumerable<CanonPiece> allPieces)
    {
        // Index pieces by composer (case-insensitive) then by title (case-insensitive)
        // for O(1) lookup.
        var index = BuildIndex(allPieces);
        var broken = new List<BrokenRef>();

        foreach (var album in albums)
        {
            foreach (var disc in album.Discs)
            {
                foreach (var track in disc.Tracks)
                {
                    if (track.PieceRefs is null) continue;

                    foreach (var pieceRef in track.PieceRefs)
                    {
                        var reason = Validate(pieceRef, index);
                        if (reason is not null)
                            broken.Add(new BrokenRef(album, disc, track, pieceRef, reason));
                    }
                }
            }
        }

        return broken;
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns null if the ref resolves, or a human-readable reason string if not.
    /// </summary>
    private static string? Validate(
        TrackPieceRef pieceRef,
        Dictionary<string, Dictionary<string, CanonPiece>> index)
    {
        if (!index.TryGetValue(pieceRef.Composer.Trim(), out var byTitle))
            return $"Composer not found: '{pieceRef.Composer}'";

        if (!byTitle.TryGetValue(pieceRef.PieceTitle.Trim(), out var piece))
            return $"Piece not found: '{pieceRef.PieceTitle}'";

        if (pieceRef.SubpiecePath is { Count: > 0 })
        {
            var reason = ValidatePath(piece, pieceRef.SubpiecePath);
            if (reason is not null) return reason;
        }

        return null;
    }

    /// <summary>
    /// Walks <paramref name="path"/> through the subpiece tree of <paramref name="piece"/>.
    /// Returns null on success or an explanatory string on the first failed step.
    /// </summary>
    private static string? ValidatePath(CanonPiece piece, IReadOnlyList<string> path)
    {
        var current = piece.Subpieces;
        var resolvedSoFar = new List<string>();

        for (var i = 0; i < path.Count; i++)
        {
            var segment = path[i];

            if (current is null or { Count: 0 })
            {
                var at = resolvedSoFar.Count > 0 ? $" at: {FormatPath(resolvedSoFar)}" : "";
                return $"Subpiece path not found -- '{piece.Title}' has no subpieces{at}";
            }

            var match = current.FirstOrDefault(s =>
                string.Equals(s.Title, segment, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                var context = resolvedSoFar.Count > 0
                    ? FormatPath(resolvedSoFar)
                    : $"'{piece.Title}'";
                return $"Subpiece not found: '{segment}' in {context}";
            }

            resolvedSoFar.Add(segment);
            current = match.Subpieces;
        }

        return null;
    }

    /// <summary>Builds a two-level case-insensitive lookup index.</summary>
    private static Dictionary<string, Dictionary<string, CanonPiece>> BuildIndex(
        IEnumerable<CanonPiece> pieces)
    {
        var index = new Dictionary<string, Dictionary<string, CanonPiece>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var piece in pieces)
        {
            var composer = piece.Composer?.Trim() ?? "";
            var title    = piece.Title?.Trim()    ?? "";
            if (string.IsNullOrEmpty(composer) || string.IsNullOrEmpty(title)) continue;

            if (!index.TryGetValue(composer, out var byTitle))
                index[composer] = byTitle = new Dictionary<string, CanonPiece>(
                    StringComparer.OrdinalIgnoreCase);

            byTitle.TryAdd(title, piece);
        }

        return index;
    }

    private static string FormatPath(IEnumerable<string> segments) =>
        string.Join(" > ", segments.Select(s => $"'{s}'"));
}
