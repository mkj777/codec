using System;
using Microsoft.UI.Xaml.Data;

namespace Codec.Helpers
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

            double hours = seconds / 3600d;
            double rounded = Math.Round(hours * 2, MidpointRounding.AwayFromZero) / 2d; // nearest half hour

            if (rounded <= 0)
            {
                return "N/A";
            }

            bool isHalfHour = Math.Abs(rounded - Math.Round(rounded)) > 0.001;
            if (!isHalfHour)
            {
                int whole = (int)Math.Round(rounded);
                return whole == 1 ? "1 Hour" : $"{whole} Hours";
            }

            int wholePart = (int)Math.Floor(rounded);
            if (wholePart <= 0)
            {
                return "½ Hour";
            }

            return $"{wholePart}½ Hours";
        }
    }
}
