using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CDArchive.Core.Models;

namespace CDArchive.App.Converters;

public class ConversionStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ConversionStatus status)
        {
            return status switch
            {
                ConversionStatus.Pending => new SolidColorBrush(Colors.Gray),
                ConversionStatus.InProgress => new SolidColorBrush(Colors.DodgerBlue),
                ConversionStatus.Completed => new SolidColorBrush(Colors.Green),
                ConversionStatus.Failed => new SolidColorBrush(Colors.Red),
                _ => new SolidColorBrush(Colors.Gray)
            };
        }

        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
