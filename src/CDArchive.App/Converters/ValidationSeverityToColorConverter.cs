using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CDArchive.Core.Models;

namespace CDArchive.App.Converters;

/// <summary>
/// Maps a <see cref="ValidationSeverity"/> to the brush registered in
/// <c>App.xaml</c> under <c>SeverityBrushXxx</c> (info severity falls
/// through to the neutral <c>StatusBrushPending</c> gray). L29 retired
/// the hardcoded <see cref="Colors.Orange"/> / <see cref="Colors.Red"/>
/// literals — palette changes now touch XAML.
/// </summary>
public class ValidationSeverityToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is ValidationSeverity severity
            ? severity switch
            {
                ValidationSeverity.Warning => "SeverityBrushWarning",
                ValidationSeverity.Error   => "SeverityBrushError",
                _                          => "StatusBrushPending",
            }
            : "StatusBrushPending";

        return Application.Current?.Resources[key] as Brush
               ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
