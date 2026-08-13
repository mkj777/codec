using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Codec.Media;

/// <summary>
/// Samples a usable accent out of a game's artwork, so covers and the details
/// page can be lit by their own art. Port of the Codec v2 reference sampler.
/// </summary>
public static class ArtColorService
{
    public static readonly Color Fallback = Color.FromRgb(0xE7, 0x6C, 0x2B);

    private const int SampleWidth = 24;
    private const int MinimumQualifyingPixels = 24;

    private static readonly ConcurrentDictionary<string, Color> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<Color>> Pending = new();

    /// <summary>Returns a cached tint immediately, or null when one has not been sampled yet.</summary>
    public static Color? Peek(string? source)
        => !string.IsNullOrWhiteSpace(source) && Cache.TryGetValue(source, out Color cached)
            ? cached
            : null;

    public static Task<Color> GetAsync(string? source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Task.FromResult(Fallback);

        if (Cache.TryGetValue(source, out Color cached))
            return Task.FromResult(cached);

        return Pending.GetOrAdd(source, key => SampleAsync(key, cancellationToken));
    }

    private static async Task<Color> SampleAsync(string source, CancellationToken cancellationToken)
    {
        try
        {
            Color color = await Task.Run(() => Sample(source), cancellationToken);
            Cache[source] = color;
            return color;
        }
        catch
        {
            Cache[source] = Fallback;
            return Fallback;
        }
        finally
        {
            Pending.TryRemove(source, out _);
        }
    }

    private static Color Sample(string source)
    {
        using Stream stream = OpenStream(source);
        using Bitmap small = Bitmap.DecodeToWidth(stream, SampleWidth, BitmapInterpolationMode.LowQuality);

        PixelSize size = small.PixelSize;
        if (size.Width <= 0 || size.Height <= 0)
            return Fallback;

        using WriteableBitmap target = new(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        byte[] pixels;
        int rowBytes;

        using (ILockedFramebuffer framebuffer = target.Lock())
        {
            small.CopyPixels(framebuffer);
            rowBytes = framebuffer.RowBytes;
            pixels = new byte[rowBytes * size.Height];
            Marshal.Copy(framebuffer.Address, pixels, 0, pixels.Length);
        }

        return Reduce(pixels, rowBytes, size);
    }

    private static Color Reduce(byte[] pixels, int rowBytes, PixelSize size)
    {
        double r = 0, g = 0, b = 0;
        int count = 0;

        for (int y = 0; y < size.Height; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < size.Width; x++)
            {
                int i = row + (x * 4);
                byte blue = pixels[i];
                byte green = pixels[i + 1];
                byte red = pixels[i + 2];

                int max = Math.Max(red, Math.Max(green, blue));
                int min = Math.Min(red, Math.Min(green, blue));
                double saturation = max == 0 ? 0 : (max - min) / (double)max;
                double luminance = (red + green + blue) / 3d;

                if (saturation > 0.22 && luminance > 45 && luminance < 225)
                {
                    r += red;
                    g += green;
                    b += blue;
                    count++;
                }
            }
        }

        // Artwork with no colourful region at all falls back to a flat average.
        if (count < MinimumQualifyingPixels)
        {
            r = g = b = 0;
            count = 0;
            for (int y = 0; y < size.Height; y++)
            {
                int row = y * rowBytes;
                for (int x = 0; x < size.Width; x++)
                {
                    int i = row + (x * 4);
                    b += pixels[i];
                    g += pixels[i + 1];
                    r += pixels[i + 2];
                    count++;
                }
            }
        }

        if (count == 0)
            return Fallback;

        double red2 = r / count;
        double green2 = g / count;
        double blue2 = b / count;

        // Lift very dark results so the tint still reads as light against the shelf.
        double lum = (red2 + green2 + blue2) / 3d;
        if (lum < 90)
        {
            double lift = 90 / Math.Max(lum, 1);
            red2 = Math.Min(255, red2 * lift);
            green2 = Math.Min(255, green2 * lift);
            blue2 = Math.Min(255, blue2 * lift);
        }

        return Color.FromRgb((byte)Math.Round(red2), (byte)Math.Round(green2), (byte)Math.Round(blue2));
    }

    private static Stream OpenStream(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using HttpClient client = new();
            byte[] bytes = client.GetByteArrayAsync(uri).GetAwaiter().GetResult();
            return new MemoryStream(bytes, writable: false);
        }

        string path = Uri.TryCreate(source, UriKind.Absolute, out uri) && uri.IsFile
            ? uri.LocalPath
            : source;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}
