using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace DbdMaxing.App.Converters;

/// <summary>Turns a local file path or URL into a frozen BitmapImage, without locking the file.</summary>
public sealed class IconSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string source || string.IsNullOrWhiteSpace(source))
            return null;
        try
        {
            var isFile = File.Exists(source);
            if (!isFile && !Uri.IsWellFormedUriString(source, UriKind.Absolute))
                return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.UriSource = new Uri(source, UriKind.Absolute);
            image.EndInit();
            if (isFile)
                image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
