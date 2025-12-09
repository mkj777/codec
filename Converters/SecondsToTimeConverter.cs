using System;
using Microsoft.UI.Xaml.Data;

namespace Codec.Converters
{
    public sealed class SecondsToTimeConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int seconds)
            {
                return Format(seconds);
            }

            return "N/A";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }

        private static string Format(int seconds)
        {
            if (seconds <= 0)
            {
                return "N/A";
            }

            var timeSpan = TimeSpan.FromSeconds(seconds);
            if (timeSpan.TotalHours >= 1)
            {
                return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m";
            }

            return $"{timeSpan.Minutes}m {timeSpan.Seconds}s";
        }
    }
}
