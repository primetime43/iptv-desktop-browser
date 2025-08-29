using System;
using System.Globalization;
using System.Windows.Data;

namespace DesktopApp.Converters;

public class NullToBoolConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool notNull = value != null;
        if (Invert) notNull = !notNull;
        return notNull;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
