using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Codec.Controls;

/// <summary>
/// Gives the cover shelf what CSS gets from `grid-template-columns: repeat(n, 1fr)`
/// plus `aspect-ratio: 2/3`: columns that fill the width, a fixed gap, and a locked
/// ratio at every size.
///
/// <see cref="UniformGridLayout"/> cannot do this alone. With
/// <c>ItemsStretch="Fill"</c> the width stretches while <c>MinItemHeight</c> stays
/// put, so the ratio drifts as the window widens. Width is therefore left to Fill,
/// which fits the row by construction, and only the height is derived here using
/// the same column count the layout will pick.
/// </summary>
public static class CoverGrid
{
    private const double CoverRatio = 1.5;

    /// <summary>
    /// Preferred cover width. The real width lands near this once columns divide
    /// evenly. Defaults to zero, not to a usable size: setting the property is what
    /// attaches the behaviour, so a default equal to the value set in XAML would
    /// raise no change and silently never attach.
    /// </summary>
    public static readonly AttachedProperty<double> TargetWidthProperty =
        AvaloniaProperty.RegisterAttached<ItemsRepeater, double>("TargetWidth", typeof(CoverGrid));

    public static readonly AttachedProperty<double> GapProperty =
        AvaloniaProperty.RegisterAttached<ItemsRepeater, double>("Gap", typeof(CoverGrid), 16d);

    public static readonly AttachedProperty<int> MinimumColumnsProperty =
        AvaloniaProperty.RegisterAttached<ItemsRepeater, int>("MinimumColumns", typeof(CoverGrid), 2);

    static CoverGrid()
    {
        TargetWidthProperty.Changed.AddClassHandler<ItemsRepeater>((repeater, _) => Attach(repeater));
        GapProperty.Changed.AddClassHandler<ItemsRepeater>((repeater, _) => Attach(repeater));
    }

    public static void SetTargetWidth(ItemsRepeater target, double value) => target.SetValue(TargetWidthProperty, value);

    public static double GetTargetWidth(ItemsRepeater target) => target.GetValue(TargetWidthProperty);

    public static void SetGap(ItemsRepeater target, double value) => target.SetValue(GapProperty, value);

    public static double GetGap(ItemsRepeater target) => target.GetValue(GapProperty);

    public static void SetMinimumColumns(ItemsRepeater target, int value) => target.SetValue(MinimumColumnsProperty, value);

    public static int GetMinimumColumns(ItemsRepeater target) => target.GetValue(MinimumColumnsProperty);

    private static void Attach(ItemsRepeater repeater)
    {
        repeater.PropertyChanged -= OnRepeaterPropertyChanged;
        repeater.PropertyChanged += OnRepeaterPropertyChanged;
        Apply(repeater);
    }

    private static void OnRepeaterPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty && sender is ItemsRepeater repeater)
            Apply(repeater);
    }

    private static void Apply(ItemsRepeater repeater)
    {
        if (repeater.Layout is not UniformGridLayout layout)
            return;

        // The repeater's own bounds can already be the inflated content width, which
        // would feed back and grow the grid past the edge. The viewport cannot.
        double available = repeater.Bounds.Width;
        if (repeater.FindAncestorOfType<ScrollViewer>() is { } scroller && scroller.Viewport.Width > 0)
            available = Math.Min(available <= 0 ? double.MaxValue : available, scroller.Viewport.Width);

        if (available <= 0 || double.IsNaN(available) || double.IsInfinity(available))
            return;

        double target = GetTargetWidth(repeater);
        double gap = GetGap(repeater);
        if (target <= 0)
            return;

        // Width is left to ItemsStretch="Fill", which fits the row by construction and
        // therefore can never overflow. Only the height is derived, using the same
        // column count the layout itself will pick, so the card lands on 2:3.
        int columns = Math.Max(GetMinimumColumns(repeater), (int)Math.Floor((available + gap) / (target + gap)));
        double width = (available - ((columns - 1) * gap)) / columns;
        if (width <= 0)
            return;

        layout.MinColumnSpacing = gap;
        layout.MinRowSpacing = gap;
        layout.MinItemWidth = target;
        layout.MinItemHeight = Math.Floor(width * CoverRatio);
    }
}
