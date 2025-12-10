using Microsoft.UI.Xaml.Data;
using System;

namespace Codec.Converters
{
    public class NullOrEmptyToFallbackConverter : IValueConverter
    {
        public string FallbackValue { get; set; } = "N/A";

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var fallback = parameter as string ?? FallbackValue;

            if (value is string s)
            {
                return string.IsNullOrWhiteSpace(s) ? fallback : s;
            }

            return value ?? fallback;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
