using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CDArchive.Core.Models;

namespace CDArchive.App.Converters;

/// <summary>
/// Maps a <see cref="ConversionStatus"/> to the brush registered in
/// <c>App.xaml</c> under <c>StatusBrushXxx</c>. L29 retired the hardcoded
/// <see cref="Colors.Gray"/> / <see cref="Colors.DodgerBlue"/> / … literals
/// that used to live in this file — palette changes now touch XAML.
/// </summary>
public class ConversionStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is ConversionStatus status
            ? status switch
            {
                ConversionStatus.Pending    => "StatusBrushPending",
                ConversionStatus.InProgress => "StatusBrushInProgress",
                ConversionStatus.Completed  => "StatusBrushCompleted",
                ConversionStatus.Failed     => "StatusBrushFailed",
                _                           => "StatusBrushPending",
            }
            : "StatusBrushPending";

        return Application.Current?.Resources[key] as Brush
               ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
