using System;
using Microsoft.UI.Xaml.Data;

namespace Codec.Converters
{
    public sealed class PercentageConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double d)
            {
                return ToPercent(d);
            }

            return "N/A";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }

        private static string ToPercent(double value)
        {
            // Accept values in range 0-1 or 0-100
            double ratio = value > 1.0 ? value / 100.0 : value;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            return string.Format("{0:P0}", ratio);
        }
    }
}
