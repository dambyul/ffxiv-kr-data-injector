using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace FFXIVInjector.UI.Converters;

public class BoolToValueConverter : IValueConverter
{
    public object? TrueValue { get; set; }
    public object? FalseValue { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? TrueValue : FalseValue;
        }
        return FalseValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
