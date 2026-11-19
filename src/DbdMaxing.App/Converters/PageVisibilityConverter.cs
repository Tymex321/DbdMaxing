using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DbdMaxing.App.Converters;

/// <summary>Visible when the bound page name equals the ConverterParameter.</summary>
public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value as string, parameter as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
