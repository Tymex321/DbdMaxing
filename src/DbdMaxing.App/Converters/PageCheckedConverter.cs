using System.Globalization;
using System.Windows.Data;

namespace DbdMaxing.App.Converters;

/// <summary>Binds a nav RadioButton's IsChecked to CurrentPage == ConverterParameter.</summary>
public sealed class PageCheckedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value as string, parameter as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}
