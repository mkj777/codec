using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Codec.Controls;

/// <summary>
/// Dissolves a control's bottom edge over a fixed distance instead of cutting it off.
///
/// A gradient brush measures its stops as fractions of the control, so a mask written in XAML
/// would fade over more pixels in a taller window. This one is anchored in pixels to the
/// bottom edge and follows the control's height, so the fade stays the same at any size.
/// </summary>
public static class EdgeFade
{
    /// <summary>How many pixels above the bottom edge the fade begins. Zero turns it off.</summary>
    public static readonly AttachedProperty<double> BottomProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Bottom", typeof(EdgeFade));

    static EdgeFade()
    {
        BottomProperty.Changed.AddClassHandler<Control, double>((control, e) =>
        {
            control.SizeChanged -= OnSizeChanged;
            if (e.NewValue.GetValueOrDefault() > 0)
            {
                control.SizeChanged += OnSizeChanged;
                Apply(control);
            }
            else
            {
                control.OpacityMask = null;
            }
        });
    }

    public static void SetBottom(Control target, double value) => target.SetValue(BottomProperty, value);

    public static double GetBottom(Control target) => target.GetValue(BottomProperty);

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control control)
            Apply(control);
    }

    private static void Apply(Control control)
    {
        double height = control.Bounds.Height;
        if (height <= 0)
            return;

        double fade = Math.Min(GetBottom(control), height);

        // The gradient pads beyond its start point, so everything above the fade stays opaque.
        if (control.OpacityMask is not LinearGradientBrush mask)
        {
            mask = new LinearGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Colors.Black, 0),
                    new GradientStop(Colors.Transparent, 1),
                },
            };
            control.OpacityMask = mask;
        }

        mask.StartPoint = new RelativePoint(0, height - fade, RelativeUnit.Absolute);
        mask.EndPoint = new RelativePoint(0, height, RelativeUnit.Absolute);
    }
}
