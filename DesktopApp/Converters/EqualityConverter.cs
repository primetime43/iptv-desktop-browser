using System;
using System.Globalization;
using System.Windows.Data;

namespace DesktopApp.Converters;

public class EqualityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2)
            return false;

        return Equals(values[0], values[1]);
    }

    public object?[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}