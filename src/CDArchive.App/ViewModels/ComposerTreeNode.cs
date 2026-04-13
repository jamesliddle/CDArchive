using System.Collections;
using System.Collections.ObjectModel;
using CDArchive.Core.Models;

namespace CDArchive.App.ViewModels;

/// <summary>
/// A display wrapper pairing a composer with their sorted, filtered pieces
/// for the master tree in CanonView.
/// </summary>
public class ComposerTreeNode
{
    public CanonComposer Composer { get; }
    public ObservableCollection<CanonPiece> Pieces { get; }

    /// <summary>
    /// Groups of works where this composer is an Other Contributor,
    /// organised by creative role and original composer.
    /// </summary>
    public List<ContributedRoleGroupNode> ContributedGroups { get; set; } = [];

    /// <summary>
    /// All tree children: own pieces followed by contributed-work groups.
    /// </summary>
    public IList AllItems
    {
        get
        {
            if (ContributedGroups.Count == 0) return Pieces;
            var list = new ArrayList(Pieces.Count + ContributedGroups.Count);
            foreach (var p in Pieces) list.Add(p);
            foreach (var g in ContributedGroups) list.Add(g);
            return list;
        }
    }

    public ComposerTreeNode(CanonComposer composer, IEnumerable<CanonPiece> pieces)
    {
        Composer = composer;
        Pieces   = new ObservableCollection<CanonPiece>(pieces);
    }
}
