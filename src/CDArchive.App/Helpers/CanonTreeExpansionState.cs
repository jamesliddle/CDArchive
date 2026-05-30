using System.Windows.Controls;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Helpers;

/// <summary>
/// Owns the per-view expansion-state HashSets for <c>CanonView</c>'s
/// composer tree and the recursive WPF walks that save / restore those
/// sets across a tree rebuild. Extracted from <c>CanonView</c>'s code-behind
/// as the first slice of H2 (the CanonView code-behind shrink).
///
/// <para>The tree has a heterogeneous structure across four "levels":</para>
/// <list type="number">
///   <item>Composers (top-level <see cref="ComposerTreeNode"/>s).</item>
///   <item>Pieces directly under a composer, cross-composer subpiece nodes,
///         and contributed-role group headers. Pieces and cross-composer
///         nodes share the <c>_expandedPieces</c> set (the cross-composer
///         node's <c>Subpiece</c> CanonPiece is the key); contributed-role
///         groups have their own set keyed on (role, composerName) tuples.</item>
///   <item>Pieces inside a contributed-role group.</item>
///   <item>Subpiece / version / "original" nodes — heterogeneous item types
///         identified by <see cref="ResolveSubpieceKey"/>; the generic
///         <see cref="TreeExpansionState"/> helper handles the recursive
///         walk.</item>
/// </list>
///
/// <para>One instance per <c>CanonView</c> lifetime. The View calls
/// <see cref="Save"/> before any operation that rebuilds the tree
/// (sort/filter change, save round-trip, modal-dialog open) and calls
/// <see cref="Restore"/> after the rebuild completes. Without the
/// save/restore dance the user's expand-state would collapse on every
/// rebuild — the very symptom <c>VirtualizingStackPanel.IsVirtualizing="False"</c>
/// used to mask before H34 retired that workaround.</para>
///
/// <para>Not headless-testable: <see cref="ItemContainerGenerator.ContainerFromItem"/>
/// requires a realised WPF visual tree. The pure-logic
/// <see cref="ResolveSubpieceKey"/> predicate is testable on its own.</para>
/// </summary>
public sealed class CanonTreeExpansionState
{
    /// <summary>Which composers are currently expanded (level 1).</summary>
    private readonly HashSet<CanonComposer> _expandedComposers =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Which pieces are currently expanded to show subpieces (level 2). Shared
    /// between owned pieces and cross-composer nodes — a cross-composer node
    /// keys its expansion on its <c>Subpiece</c> CanonPiece, so expanding
    /// "Fanfare" under Ravel and under (Various) stays consistent.
    /// </summary>
    private readonly HashSet<CanonPiece> _expandedPieces =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Which subpiece/version nodes are expanded (level 3+), keyed by model object.</summary>
    private readonly HashSet<object> _expandedSubpieces =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Which contributed-role group headers are expanded, keyed by (role, composerName).</summary>
    private readonly HashSet<(string, string)> _expandedContributedGroups = [];

    /// <summary>
    /// Walks the live tree and records which composer / piece / cross-composer /
    /// contributed-group / subpiece-or-version containers are expanded. Clears
    /// the previous state before writing — every call is a full snapshot.
    /// </summary>
    public void Save(TreeView composerTree)
    {
        _expandedComposers.Clear();
        _expandedPieces.Clear();
        _expandedSubpieces.Clear();
        _expandedContributedGroups.Clear();

        if (composerTree.ItemsSource is not IEnumerable<ComposerTreeNode> nodes) return;

        foreach (var node in nodes)
        {
            if (composerTree.ItemContainerGenerator.ContainerFromItem(node)
                    is not TreeViewItem ci) continue;

            if (ci.IsExpanded)
                _expandedComposers.Add(node.Composer);

            // Level 2: pieces under this composer
            foreach (var piece in node.Pieces)
            {
                if (ci.ItemContainerGenerator.ContainerFromItem(piece)
                        is not TreeViewItem pi) continue;

                if (pi.IsExpanded)
                    _expandedPieces.Add(piece);

                CollectExpandedSubpieces(pi, pi.Items);
            }

            // Cross-composer subpiece nodes (collaborative-work entries) live
            // alongside owned pieces in AllItems. We key their expansion on
            // .Subpiece (a CanonPiece instance) using the same _expandedPieces
            // set — that way expanding "Fanfare" under Ravel and under
            // (Various) stays consistent.
            foreach (var ccn in node.CrossComposerNodes)
            {
                if (ci.ItemContainerGenerator.ContainerFromItem(ccn)
                        is not TreeViewItem cni) continue;

                if (cni.IsExpanded)
                    _expandedPieces.Add(ccn.Subpiece);

                CollectExpandedSubpieces(cni, cni.Items);
            }

            // Contributed-work groups
            foreach (var group in node.ContributedGroups)
            {
                if (ci.ItemContainerGenerator.ContainerFromItem(group)
                        is not TreeViewItem gi) continue;

                if (gi.IsExpanded)
                    _expandedContributedGroups.Add((group.Role, group.ComposerName));

                foreach (var contribPiece in group.Pieces)
                {
                    if (gi.ItemContainerGenerator.ContainerFromItem(contribPiece)
                            is not TreeViewItem cpi) continue;

                    if (cpi.IsExpanded)
                        _expandedPieces.Add(contribPiece.Piece);

                    CollectExpandedSubpieces(cpi, cpi.Items);
                }
            }
        }
    }

    /// <summary>
    /// After the tree has been rebuilt, re-expands composers, pieces, and
    /// subpiece nodes that were previously expanded. The early-out when every
    /// set is empty avoids a UI nudge on first load.
    /// </summary>
    public void Restore(TreeView composerTree, IEnumerable<ComposerTreeNode> nodes)
    {
        if (_expandedComposers.Count == 0 && _expandedPieces.Count == 0
            && _expandedContributedGroups.Count == 0) return;
        composerTree.UpdateLayout();

        foreach (var node in nodes)
        {
            if (!_expandedComposers.Contains(node.Composer)) continue;
            if (composerTree.ItemContainerGenerator.ContainerFromItem(node)
                    is not TreeViewItem ci) continue;

            ci.IsExpanded = true;
            ci.UpdateLayout();

            foreach (var piece in node.Pieces)
            {
                if (!_expandedPieces.Contains(piece)) continue;
                if (ci.ItemContainerGenerator.ContainerFromItem(piece)
                        is not TreeViewItem pi) continue;

                pi.IsExpanded = true;
                pi.UpdateLayout();
                ApplyExpandedSubpieces(pi, pi.Items);
            }

            // Restore cross-composer node expansion (keyed by .Subpiece in
            // _expandedPieces, mirroring the save pass).
            foreach (var ccn in node.CrossComposerNodes)
            {
                if (!_expandedPieces.Contains(ccn.Subpiece)) continue;
                if (ci.ItemContainerGenerator.ContainerFromItem(ccn)
                        is not TreeViewItem cni) continue;

                cni.IsExpanded = true;
                cni.UpdateLayout();
                ApplyExpandedSubpieces(cni, cni.Items);
            }

            // Restore contributed-group expansion
            foreach (var group in node.ContributedGroups)
            {
                if (!_expandedContributedGroups.Contains((group.Role, group.ComposerName))) continue;
                if (ci.ItemContainerGenerator.ContainerFromItem(group)
                        is not TreeViewItem gi) continue;

                gi.IsExpanded = true;
                gi.UpdateLayout();

                foreach (var contribPiece in group.Pieces)
                {
                    if (!_expandedPieces.Contains(contribPiece.Piece)) continue;
                    if (gi.ItemContainerGenerator.ContainerFromItem(contribPiece)
                            is not TreeViewItem cpi) continue;

                    cpi.IsExpanded = true;
                    cpi.UpdateLayout();
                    ApplyExpandedSubpieces(cpi, cpi.Items);
                }
            }
        }
    }

    // ── Level 3+ subpiece walks ─────────────────────────────────────────────
    // Heterogeneous item types (SubpieceDisplayNode / VersionDisplayNode /
    // PieceOriginalNode) — H47 already factored the recursive walk into the
    // generic TreeExpansionState helper. This class only owns the predicate
    // that knows about CanonView's item-type vocabulary.

    private void CollectExpandedSubpieces(ItemsControl parent, ItemCollection items) =>
        TreeExpansionState.CollectExpanded(parent, items, ResolveSubpieceKey, _expandedSubpieces);

    private void ApplyExpandedSubpieces(ItemsControl parent, ItemCollection items) =>
        TreeExpansionState.ApplyExpanded(parent, items, ResolveSubpieceKey, _expandedSubpieces);

    /// <summary>
    /// Maps a subpiece-area item to its expansion-key. Pure logic, exposed for
    /// unit testing; consumed via delegate by <see cref="TreeExpansionState"/>.
    /// The (Piece, "original") value-tuple for <see cref="PieceOriginalNode"/>
    /// is a struct, so equality is value-based and survives tree rebuilds.
    /// </summary>
    public static object? ResolveSubpieceKey(object item) => item switch
    {
        SubpieceDisplayNode n => n.Piece,
        VersionDisplayNode  v => (object)v.Version,
        PieceOriginalNode   o => (o.Piece, "original"),
        _                     => null,
    };
}
