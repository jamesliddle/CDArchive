using System.Collections;
using System.Collections.ObjectModel;
using CDArchive.Core.Models;

namespace CDArchive.App.ViewModels;

/// <summary>
/// A display wrapper pairing a composer with their pieces for the master tree
/// in CanonView.
/// <para>
/// The owned <see cref="Pieces"/>, <see cref="CrossComposerNodes"/>, and
/// <see cref="ContributedGroups"/> collections are exposed for navigation
/// (expansion-state save/restore, hit aggregation, etc.). The tree binds to
/// <see cref="AllItems"/>, which is built once by the caller — using
/// <see cref="PieceSorting.Sort"/> to honour the selected sort field — so the
/// node does not silently re-sort and override the user's choice.
/// </para>
/// </summary>
public class ComposerTreeNode
{
    public CanonComposer Composer { get; }

    /// <summary>Top-level pieces this composer owns directly.</summary>
    public ObservableCollection<CanonPiece> Pieces { get; }

    /// <summary>
    /// Cross-credit nodes — collaborative-work subpieces (e.g. Ravel's Fanfare
    /// from <em>L'éventail de Jeanne</em>) where this composer wrote a part of
    /// a work whose top-level composer is somebody else.
    /// </summary>
    public List<CrossComposerSubpieceNode> CrossComposerNodes { get; set; } = [];

    /// <summary>
    /// Groups of works where this composer is an Other Contributor, organised
    /// by creative role and original composer.
    /// </summary>
    public List<ContributedRoleGroupNode> ContributedGroups { get; set; } = [];

    /// <summary>
    /// The merged, sorted children shown in the tree:
    /// owned pieces and cross-credit nodes interleaved by the selected sort,
    /// followed by contributed-work group headers.
    /// <para>
    /// Defaults to a Catalogue-sorted merge — matches the
    /// <c>SelectedIndex="0"</c> default on the Sort Pieces combo. Callers
    /// override via <see cref="RebuildAllItems"/> when the user picks a
    /// different sort field.
    /// </para>
    /// </summary>
    public IList AllItems { get; private set; } = new List<object>();

    public ComposerTreeNode(CanonComposer composer, IEnumerable<CanonPiece> pieces)
    {
        Composer = composer;
        Pieces   = new ObservableCollection<CanonPiece>(pieces);
        // Default AllItems to a catalogue-sorted view of the owned pieces;
        // CanonView.ApplySortedFilter calls RebuildAllItems with the right
        // field once cross-credit and contributor groups are attached.
        RebuildAllItems(PieceSortField.Catalogue);
    }

    /// <summary>
    /// Rebuilds <see cref="AllItems"/> using the given sort field. The merge
    /// of owned pieces and cross-credit nodes goes through
    /// <see cref="PieceSorting.Sort"/>; contributed-work groups are appended
    /// at the end (their ordering is intrinsic to the group, not affected
    /// by the piece sort).
    /// <para>
    /// <paramref name="recordingCount"/> is required only for
    /// <see cref="PieceSortField.Recordings"/>; for other fields it is
    /// ignored.
    /// </para>
    /// </summary>
    public void RebuildAllItems(PieceSortField field, Func<object, int>? recordingCount = null)
    {
        var sorted = PieceSorting.Sort(Pieces, CrossComposerNodes, field, recordingCount);
        if (ContributedGroups.Count > 0)
            sorted.AddRange(ContributedGroups);
        AllItems = sorted;
    }
}
