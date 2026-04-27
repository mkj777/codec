using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace Codec.Helpers
{
    public class IntEqualsToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is int i && parameter is string s && int.TryParse(s, out int p) && i == p;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b && b && parameter is string s && int.TryParse(s, out int p))
                return p;
            return DependencyProperty.UnsetValue;
        }
    }
}
