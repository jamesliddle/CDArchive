using System.Globalization;
using System.Windows.Data;

namespace CDArchive.App.Converters;

/// <summary>
/// MultiBinding converter that returns true iff the two bound values refer to
/// the same instance (reference equality). Used to highlight the
/// currently-playing track row in the AlbumEditorWindow track list —
/// MultiBinding lets a row reactively compare its own <c>Track</c> against
/// <c>PlayerViewModel.CurrentTrack</c> without polling.
/// </summary>
public sealed class ReferenceEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        return ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
