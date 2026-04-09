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

    public ComposerTreeNode(CanonComposer composer, IEnumerable<CanonPiece> pieces)
    {
        Composer = composer;
        Pieces   = new ObservableCollection<CanonPiece>(pieces);
    }
}
