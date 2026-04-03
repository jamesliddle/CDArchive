using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CDArchive.Core.Models;

namespace CDArchive.App.Converters;

public class ValidationSeverityToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ValidationSeverity severity)
        {
            return severity switch
            {
                ValidationSeverity.Warning => new SolidColorBrush(Colors.Orange),
                ValidationSeverity.Error => new SolidColorBrush(Colors.Red),
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
