using System;
using System.Globalization;
using Avalonia.Data.Converters;
using FFXIVInjector.Core;
using FFXIVInjector.UI.Services;

namespace FFXIVInjector.UI.Converters;

public class LanguageDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is GameLanguage lang)
        {
            return LocalizationService.Instance.GetString($"Language.{lang}");
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
