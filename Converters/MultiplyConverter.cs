using Microsoft.UI.Xaml.Data;
using System;
using System.Globalization;

namespace Codec.Converters
{
    public sealed class MultiplyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double input && parameter is not null)
            {
                if (double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var factor))
                {
                    return input * factor;
                }
            }

            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
