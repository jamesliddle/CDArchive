using System.Globalization;
using System.Windows.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CDArchive.App.ViewModels;

namespace CDArchive.App.Converters;

/// <summary>
/// Returns a parenthesised album-hit count (e.g. "(3)") for a Canon tree node,
/// or an empty string when no albums reference that node. Zero-count nodes
/// should not render a badge, which keeps the tree visually uncluttered for
/// pieces that aren't in the catalogue yet.
/// </summary>
/// <remarks>
/// The converter reads <see cref="PieceReferenceIndex.Current"/> — a static
/// accessor rather than a DI dependency, because WPF value converters are
/// constructed from XAML and can't be injected.
/// </remarks>
public class HitCountBadgeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var idx = PieceReferenceIndex.Current;
        if (idx is null || value is null) return "";

        int count = value switch
        {
            ComposerTreeNode n        => idx.CountForComposer(n.Composer.Name),
            CanonComposer c           => idx.CountForComposer(c.Name),
            PieceOriginalNode pon     => idx.CountForOriginal(pon.Piece),
            VersionDisplayNode vdn    => idx.CountForVersion(vdn.Version),
            SubpieceDisplayNode sdn   => idx.CountForPiece(sdn.Piece),
            ContributedPieceNode cpn  => idx.CountForPiece(cpn.Piece),
            ContributedRoleGroupNode g=> idx.CountForPieces(g.Pieces.Select(p => p.Piece)),
            CanonPiece p              => idx.CountForPiece(p),
            _ => 0
        };

        return count > 0 ? $"({count})" : "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
