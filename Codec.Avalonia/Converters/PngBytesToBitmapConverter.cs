using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using System.Globalization;

namespace Codec.Converters;

public sealed class PngBytesToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] png || png.Length == 0)
            return null;

        using var stream = new MemoryStream(png, writable: false);
        return new Bitmap(stream);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
