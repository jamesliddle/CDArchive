using System.Globalization;
using System.Windows;
using System.Windows.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.App.Converters;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> when the bound value's resolved
/// path defines variants but identifies none — the "variant available but
/// unchosen" state — otherwise <see cref="Visibility.Collapsed"/>. Accepts a
/// single <see cref="TrackPieceRef"/> (TrackEditor piece-refs list) or a whole
/// <see cref="AlbumTrack"/> (AlbumEditor track list — flagged when ANY of its
/// refs needs a variant). Drives the unchosen-variant flag.
/// </summary>
/// <remarks>
/// Reads <see cref="PieceReferenceIndex.Current"/> (a static accessor, not a DI
/// dependency) because WPF value converters are constructed from XAML — the same
/// pattern as <see cref="HitCountBadgeConverter"/>.
/// </remarks>
public class RefNeedsVariantConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var idx = PieceReferenceIndex.Current;
        if (idx is null) return Visibility.Collapsed;

        bool needs = value switch
        {
            TrackPieceRef pr => idx.NeedsVariantIdentification(pr),
            AlbumTrack t     => t.PieceRefs is { Count: > 0 }
                                && t.PieceRefs.Any(idx.NeedsVariantIdentification),
            _                => false,
        };
        return needs ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
