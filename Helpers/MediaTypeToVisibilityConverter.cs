using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Codec.Converters
{
    public sealed class MediaTypeToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var uri = value as string;
            var mode = parameter as string;
            bool isVideo = IsVideo(uri);

            return (mode?.Equals("video", StringComparison.OrdinalIgnoreCase) == true)
                ? (isVideo ? Visibility.Visible : Visibility.Collapsed)
                : (isVideo ? Visibility.Collapsed : Visibility.Visible);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }

        private static bool IsVideo(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            uri = uri.ToLowerInvariant();
            return uri.Contains(".mpd") || uri.Contains(".mp4") || uri.Contains(".webm");
        }
    }
}
