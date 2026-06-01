using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CDArchive.App.Converters;

/// <summary>
/// Returns the red <c>DangerBrush</c> from <c>App.xaml</c> when the bound
/// <c>IsProvisional</c> bool is true; otherwise returns
/// <see cref="DependencyProperty.UnsetValue"/> so the TextBlock inherits its
/// default <c>Foreground</c>. Replaces the per-view "(provisional)" badge
/// TextBlocks with in-place red colouring.
/// </summary>
public class IsProvisionalToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return Application.Current?.Resources["DangerBrush"] as Brush
                   ?? new SolidColorBrush(Colors.Red);
        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
