using CDArchive.Core.Models;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Applies a set of <see cref="PieceRename"/> records (produced by
/// <see cref="PieceRefPathDiffer.Diff"/>) to every <see cref="TrackPieceRef"/>
/// across a collection of <see cref="CanonAlbum"/>s.
/// </summary>
/// <remarks>
/// Renames are sorted deepest-first before application so that cascading
/// parent+child renames within a single edit do not interfere with each other.
/// </remarks>
public static class AlbumRefUpdater
{
    /// <summary>
    /// Patches all stale <see cref="TrackPieceRef"/>s in <paramref name="albums"/>
    /// according to <paramref name="renames"/> and returns the number of references
    /// that were updated.  Returns 0 when the rename list is empty.
    /// </summary>
    public static int ApplyRenames(
        IEnumerable<CanonAlbum> albums,
        IReadOnlyList<PieceRename> renames)
    {
        if (renames.Count == 0) return 0;

        // Sort deepest (most-specific) paths first so a child rename is applied
        // before its parent rename; this prevents a parent rename from invalidating
        // a child rename's OldSubpiecePath match within the same edit.
        var sorted = renames
            .OrderByDescending(r => r.OldSubpiecePath?.Count ?? -1)
            .ToList();

        var count = 0;
        foreach (var album in albums)
            foreach (var disc in album.Discs)
                foreach (var track in disc.Tracks)
                    foreach (var pieceRef in track.AllPieceRefs())
                        foreach (var rename in sorted)
                            if (ApplyRename(pieceRef, rename))
                                count++;
        return count;
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to apply a single <see cref="PieceRename"/> to <paramref name="pieceRef"/>.
    /// Returns true if the ref was modified.
    /// </summary>
    private static bool ApplyRename(TrackPieceRef pieceRef, PieceRename rename)
    {
        // Composer must match (case-insensitive).
        if (!string.Equals(pieceRef.Composer, rename.Composer,
                StringComparison.OrdinalIgnoreCase))
            return false;

        // Piece title must match the old title (case-insensitive).
        if (!string.Equals(pieceRef.PieceTitle, rename.OldPieceTitle,
                StringComparison.OrdinalIgnoreCase))
            return false;

        bool changed = false;

        if (rename.OldSubpiecePath is null)
        {
            // ── Top-level piece title rename ─────────────────────────────────
            // Update the piece title; subpiece path is unaffected.
            pieceRef.PieceTitle = rename.NewPieceTitle;
            changed = true;
        }
        else
        {
            // ── Subpiece rename ──────────────────────────────────────────────
            // The ref's SubpiecePath must equal OldSubpiecePath or start with it
            // (the latter covers refs pointing at children of the renamed node).
            var refPath = pieceRef.SubpiecePath;
            if (refPath is null or { Count: 0 }) return false;

            var oldPath = rename.OldSubpiecePath;
            var newPath = rename.NewSubpiecePath!;

            if (!HasPrefix(refPath, oldPath)) return false;

            // Replace the matching prefix with the new path.
            var updated = new List<string>(newPath);
            updated.AddRange(refPath.Skip(oldPath.Count));
            pieceRef.SubpiecePath = updated;

            // Also update the piece title if it changed (OldPieceTitle ≠ NewPieceTitle)
            // — avoids emitting a separate top-level rename record for the piece title
            // when we already have subpiece renames for the same edit.
            if (!string.Equals(rename.OldPieceTitle, rename.NewPieceTitle,
                    StringComparison.OrdinalIgnoreCase))
                pieceRef.PieceTitle = rename.NewPieceTitle;

            changed = true;
        }

        return changed;
    }

    /// <summary>Returns true if <paramref name="path"/> starts with all elements of <paramref name="prefix"/>.</summary>
    private static bool HasPrefix(
        IReadOnlyList<string> path,
        IReadOnlyList<string> prefix)
    {
        if (prefix.Count > path.Count) return false;
        for (var i = 0; i < prefix.Count; i++)
            if (!string.Equals(path[i], prefix[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}

/// <summary>Extension helpers for iterating piece refs across track structures.</summary>
internal static class AlbumTrackExtensions
{
    /// <summary>
    /// Returns the effective piece ref list for a track: the track's own
    /// <see cref="AlbumTrack.PieceRefs"/> (never null-safe; callers check IsCatalogued).
    /// </summary>
    internal static IEnumerable<TrackPieceRef> AllPieceRefs(this AlbumTrack track) =>
        track.PieceRefs ?? [];
}
