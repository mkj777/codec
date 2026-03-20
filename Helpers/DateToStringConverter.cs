using Microsoft.UI.Xaml.Data;
using System;
using System.Globalization;

namespace Codec.Converters
{
    public sealed class DateToStringConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is DateTime dt)
            {
                return dt.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);
            }

            return "N/A";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
