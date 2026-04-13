using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace Codec.Helpers
{
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool flag)
            {
                bool reverse = parameter is string s && s.Equals("Reverse", StringComparison.OrdinalIgnoreCase);
                bool visible = reverse ? !flag : flag;
                return visible ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
