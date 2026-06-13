using System.Globalization;
using System.Windows;
using System.Windows.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.App.Converters;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> for a <see cref="TrackPieceRef"/>
/// whose resolved path defines variants but identifies none — the
/// "variant available but unchosen" state — otherwise
/// <see cref="Visibility.Collapsed"/>. Drives the unchosen-variant flag on the
/// TrackEditor's piece-refs list.
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
        if (idx is not null && value is TrackPieceRef pr && idx.NeedsVariantIdentification(pr))
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
