using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Data;

namespace Codec.Helpers
{
    public sealed class ListToCommaConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is IEnumerable<string> list)
            {
                var items = list.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                return items.Length > 0 ? string.Join(", ", items) : "N/A";
            }

            return "N/A";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
