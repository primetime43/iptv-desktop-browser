using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DesktopApp.Converters;

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isNull = value == null || (value is string str && string.IsNullOrWhiteSpace(str));
        bool invert = parameter?.ToString() == "Invert";

        if (invert)
        {
            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            return isNull ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
