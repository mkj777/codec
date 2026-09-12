using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using System.Collections;
using System.Globalization;

namespace Codec.Converters;

public sealed class BooleanNegationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag && !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag ? !flag : AvaloniaProperty.UnsetValue;
}

public sealed class BooleanToDoubleConverter : IValueConverter
{
    public double Expanded { get; set; } = 240;

    public double Collapsed { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Collapsed : Expanded;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Picks one of two integers. The franchise timeline uses it to place an entry above or
/// below the line without carrying the whole card twice.
/// </summary>
public sealed class BooleanToIntConverter : IValueConverter
{
    public int True { get; set; }

    public int False { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? True : False;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BytesToSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes || bytes <= 0)
            return "Unknown size";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        double size = bytes;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DateToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime date
            ? date.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture)
            : "N/A";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int actual && TryGetExpected(parameter, out int expected) && actual == expected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && TryGetExpected(parameter, out int expected)
            ? expected
            : AvaloniaProperty.UnsetValue;

    private static bool TryGetExpected(object? parameter, out int expected)
        => int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out expected);
}

public sealed class ListToCommaConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable<string> values)
        {
            string[] items = values.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            return items.Length == 0 ? "N/A" : string.Join(", ", items);
        }

        return "N/A";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class MediaTypeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isVideo = IsVideo(value as string);
        return string.Equals(parameter as string, "video", StringComparison.OrdinalIgnoreCase)
            ? isVideo
            : !isVideo;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static bool IsVideo(string? uri)
        => !string.IsNullOrWhiteSpace(uri) &&
           (uri.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains(".webm", StringComparison.OrdinalIgnoreCase));
}

public sealed class MultiplyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double input &&
           double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double factor)
            ? input * factor
            : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullOrEmptyToFallbackConverter : IValueConverter
{
    public string FallbackValue { get; set; } = "N/A";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string fallback = parameter as string ?? FallbackValue;
        if (value is string text)
            return string.IsNullOrWhiteSpace(text) ? fallback : text;

        if (value is IEnumerable values)
        {
            IEnumerator enumerator = values.GetEnumerator();
            try
            {
                return enumerator.MoveNext() ? value : fallback;
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        return value ?? fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value != null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// True when a string actually says something. Several of the display properties on
/// <c>Game</c> answer with an empty string rather than null when they have nothing, so
/// <see cref="NullToBoolConverter"/> would keep their rows on screen holding nothing.
/// </summary>
public sealed class HasTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrWhiteSpace(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PercentageConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double number)
            return "N/A";

        double ratio = number > 1 ? number / 100 : number;
        return Math.Clamp(ratio, 0, 1).ToString("P0", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int seconds || seconds <= 0)
            return "N/A";

        double roundedHours = Math.Round(seconds / 1800d, MidpointRounding.AwayFromZero) / 2d;
        if (roundedHours <= 0)
            return "N/A";

        int wholeHours = (int)Math.Floor(roundedHours);
        bool hasHalfHour = Math.Abs(roundedHours - Math.Round(roundedHours)) > 0.001;
        if (!hasHalfHour)
            return wholeHours == 1 ? "1 Hour" : $"{wholeHours} Hours";

        return wholeHours == 0 ? "½ Hour" : $"{wholeHours}½ Hours";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringToUriConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text && Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            ? uri
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Picks the first usable string. Cinema prefers the landscape hero and falls
/// back to the capsule when a game has no hero art.
/// </summary>
public sealed class FirstNonEmptyConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        foreach (object? value in values)
        {
            if (value is string text && !string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }
}

public sealed class DateToYearConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime date && date != default
            ? date.Year.ToString(CultureInfo.InvariantCulture)
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True when the bound collection has at least one entry.</summary>
public sealed class FirstItemConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable items)
            return null;

        foreach (object? item in items)
            return item;

        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
