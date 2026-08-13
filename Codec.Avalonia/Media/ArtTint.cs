using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Codec.Media;

/// <summary>
/// Lights a surface with a colour sampled from the artwork it belongs to.
/// Sampling is asynchronous, so each target keeps a token and only the most
/// recent request is allowed to paint.
/// </summary>
public static class ArtTint
{
    /// <summary>Paints a hover ring and glow on a border, in the artwork's own colour.</summary>
    public static readonly AttachedProperty<string?> RingSourceProperty =
        AvaloniaProperty.RegisterAttached<Border, string?>("RingSource", typeof(ArtTint));

    /// <summary>Fills a border with the cinema radial, rising from the bottom centre.</summary>
    public static readonly AttachedProperty<string?> GlowSourceProperty =
        AvaloniaProperty.RegisterAttached<Border, string?>("GlowSource", typeof(ArtTint));

    private static readonly AttachedProperty<long> VersionProperty =
        AvaloniaProperty.RegisterAttached<Border, long>("Version", typeof(ArtTint));

    private static long _version;

    static ArtTint()
    {
        RingSourceProperty.Changed.AddClassHandler<Border>((border, e) =>
            Apply(border, e.NewValue as string, ApplyRing));

        GlowSourceProperty.Changed.AddClassHandler<Border>((border, e) =>
            Apply(border, e.NewValue as string, ApplyGlow));
    }

    public static void SetRingSource(Border target, string? value) => target.SetValue(RingSourceProperty, value);

    public static string? GetRingSource(Border target) => target.GetValue(RingSourceProperty);

    public static void SetGlowSource(Border target, string? value) => target.SetValue(GlowSourceProperty, value);

    public static string? GetGlowSource(Border target) => target.GetValue(GlowSourceProperty);

    private static void Apply(Border border, string? source, Action<Border, Color> paint)
    {
        long version = Interlocked.Increment(ref _version);
        border.SetValue(VersionProperty, version);

        if (string.IsNullOrWhiteSpace(source))
        {
            paint(border, ArtColorService.Fallback);
            return;
        }

        if (ArtColorService.Peek(source) is { } cached)
        {
            paint(border, cached);
            return;
        }

        paint(border, ArtColorService.Fallback);

        _ = ArtColorService.GetAsync(source).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully)
                return;

            Color color = task.Result;
            Dispatcher.UIThread.Post(() =>
            {
                // A recycled container may already point at different artwork.
                if (border.GetValue(VersionProperty) == version)
                    paint(border, color);
            });
        }, TaskScheduler.Default);
    }

    private static void ApplyRing(Border border, Color color)
    {
        border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x8C, color.R, color.G, color.B));
        border.BoxShadow = new BoxShadows(new BoxShadow
        {
            OffsetX = 0,
            OffsetY = 0,
            Blur = 34,
            Spread = -8,
            Color = Color.FromArgb(0x80, color.R, color.G, color.B),
        });
    }

    private static void ApplyGlow(Border border, Color color)
    {
        border.Background = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 1.06, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 1.06, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(1.0, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x42, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 0.66),
            },
        };
    }
}
