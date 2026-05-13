using CDArchive.Core.Models;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Snapshots the title structure of a <see cref="CanonPiece"/> before an edit, then
/// diffs the snapshot against the post-edit piece to produce a list of
/// <see cref="PieceRename"/> records.
/// </summary>
/// <remarks>
/// <para>
/// Matching is position-based: subpiece[i] before edit corresponds to subpiece[i] after.
/// Title changes at any depth are detected; structural changes (additions, deletions,
/// reorderings) are not auto-fixed — they will surface as broken refs in
/// <see cref="AlbumConsistencyChecker.FindBrokenRefs"/>.
/// </para>
/// <para>
/// Renames are emitted using the original (pre-edit) old path and the final (post-edit)
/// new path at each level, including any ancestor-level renames that occurred in the same
/// edit.  <see cref="AlbumRefUpdater"/> applies the deepest renames first so that
/// cascading parent+child renames within a single edit are handled correctly.
/// </para>
/// </remarks>
public static class PieceRefPathDiffer
{
    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the composer name, piece title, and the full recursive subpiece-path
    /// structure of <paramref name="piece"/> before it is edited.
    /// Call this immediately before opening the editor dialog.
    /// </summary>
    public static PiecePathSnapshot Snapshot(CanonPiece piece) =>
        new(piece.Composer ?? "", piece.Title ?? "", SnapshotSubpieces(piece.Subpieces));

    /// <summary>
    /// Compares <paramref name="before"/> (captured before editing) against
    /// <paramref name="after"/> (the same object after the editor closed with OK)
    /// and returns one <see cref="PieceRename"/> for every title that changed.
    /// Returns an empty sequence when nothing changed.
    /// </summary>
    public static IReadOnlyList<PieceRename> Diff(PiecePathSnapshot before, CanonPiece after)
    {
        var renames = new List<PieceRename>();
        var composer = before.Composer;                  // composer never changes in a piece edit
        var oldTitle = before.PieceTitle;
        var newTitle = after.Title ?? "";

        if (!string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
            renames.Add(new PieceRename(composer, oldTitle, newTitle, null, null));

        // Diff subpiece trees, tracking both old and new path prefixes separately
        // so the emitted records contain correct before/after paths even when
        // a parent and child are both renamed in the same edit.
        DiffSubpieces(
            composer,
            oldTitle, newTitle,
            before.SubpieceTree, after.Subpieces,
            oldPrefix: [],
            newPrefix: [],
            renames);

        return renames;
    }

    // ── Internal snapshot helpers ────────────────────────────────────────────

    /// <summary>
    /// Recursively builds a tree snapshot (just titles, no other piece data).
    /// </summary>
    private static List<SubpieceSnapshot> SnapshotSubpieces(List<CanonPiece>? subpieces)
    {
        if (subpieces is null or { Count: 0 }) return [];
        return subpieces
            .Select(s => new SubpieceSnapshot(s.Title ?? "", SnapshotSubpieces(s.Subpieces)))
            .ToList();
    }

    // ── Internal diff helpers ────────────────────────────────────────────────

    private static void DiffSubpieces(
        string composer,
        string oldPieceTitle,
        string newPieceTitle,
        IReadOnlyList<SubpieceSnapshot> oldSubs,
        List<CanonPiece>? newSubsList,
        IReadOnlyList<string> oldPrefix,
        IReadOnlyList<string> newPrefix,
        List<PieceRename> renames)
    {
        var newSubs = newSubsList ?? [];
        var count = Math.Min(oldSubs.Count, newSubs.Count);

        for (var i = 0; i < count; i++)
        {
            var oldSnap = oldSubs[i];
            var newSub  = newSubs[i];
            var oldTitle = oldSnap.Title;
            var newTitle = newSub.Title ?? "";

            // Build the full old and new paths to this node.
            var oldPath = Append(oldPrefix, oldTitle);
            var newPath = Append(newPrefix, newTitle);

            if (!string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
            {
                renames.Add(new PieceRename(
                    composer,
                    oldPieceTitle, newPieceTitle,
                    oldPath, newPath));
            }

            // Recurse using the path to this node as the next prefix —
            // both old and new, keeping them independent.
            DiffSubpieces(
                composer,
                oldPieceTitle, newPieceTitle,
                oldSnap.Children, newSub.Subpieces,
                oldPath, newPath,
                renames);
        }
    }

    private static IReadOnlyList<string> Append(IReadOnlyList<string> prefix, string segment)
    {
        var list = new List<string>(prefix.Count + 1);
        list.AddRange(prefix);
        list.Add(segment);
        return list;
    }
}

// ── Supporting types ─────────────────────────────────────────────────────────

/// <summary>
/// Immutable snapshot of a piece's title and subpiece-path tree, captured
/// before an edit so that <see cref="PieceRefPathDiffer.Diff"/> can compare.
/// </summary>
public record PiecePathSnapshot(
    string Composer,
    string PieceTitle,
    IReadOnlyList<SubpieceSnapshot> SubpieceTree
);

/// <summary>
/// One node in a <see cref="PiecePathSnapshot"/> subpiece tree.
/// Only titles are captured — enough for path-based diffing.
/// </summary>
public record SubpieceSnapshot(
    string Title,
    IReadOnlyList<SubpieceSnapshot> Children
);
