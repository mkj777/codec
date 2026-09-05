using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace Codec.Controls;

/// <summary>
/// Codec v2 progress. A one pixel line that sweeps while work is in flight,
/// fills from the left where a real percentage exists, and goes solid when done.
/// Replaces ProgressBar everywhere in the application.
/// </summary>
public sealed class Hairline : TemplatedControl
{
    private const double SweepFraction = 0.4;

    private Control? _sweep;
    private CancellationTokenSource? _sweepCancellation;
    private double _runningSweepWidth = double.NaN;

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Hairline, double>(nameof(Value));

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<Hairline, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Hairline, double>(nameof(Maximum), 1d);

    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<Hairline, bool>(nameof(IsIndeterminate));

    public static readonly StyledProperty<bool> IsCompleteProperty =
        AvaloniaProperty.Register<Hairline, bool>(nameof(IsComplete));

    public static readonly DirectProperty<Hairline,double> FillWidthProperty =
        AvaloniaProperty.RegisterDirect<Hairline, double>(nameof(FillWidth), o => o.FillWidth);

    public static readonly DirectProperty<Hairline,double> SweepWidthProperty =
        AvaloniaProperty.RegisterDirect<Hairline, double>(nameof(SweepWidth), o => o.SweepWidth);

    public static readonly DirectProperty<Hairline,bool> ShowFillProperty =
        AvaloniaProperty.RegisterDirect<Hairline, bool>(nameof(ShowFill), o => o.ShowFill);

    public static readonly DirectProperty<Hairline,bool> ShowSweepProperty =
        AvaloniaProperty.RegisterDirect<Hairline, bool>(nameof(ShowSweep), o => o.ShowSweep);

    private double _fillWidth;
    private double _sweepWidth;
    private bool _showFill;
    private bool _showSweep;

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public bool IsComplete
    {
        get => GetValue(IsCompleteProperty);
        set => SetValue(IsCompleteProperty, value);
    }

    public double FillWidth
    {
        get => _fillWidth;
        private set => SetAndRaise(FillWidthProperty, ref _fillWidth, value);
    }

    public double SweepWidth
    {
        get => _sweepWidth;
        private set => SetAndRaise(SweepWidthProperty, ref _sweepWidth, value);
    }

    public bool ShowFill
    {
        get => _showFill;
        private set => SetAndRaise(ShowFillProperty, ref _showFill, value);
    }

    public bool ShowSweep
    {
        get => _showSweep;
        private set => SetAndRaise(ShowSweepProperty, ref _showSweep, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _sweep = e.NameScope.Find<Control>("PART_Sweep");
        StopSweep();
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty ||
            change.Property == MinimumProperty ||
            change.Property == MaximumProperty ||
            change.Property == IsIndeterminateProperty ||
            change.Property == IsCompleteProperty ||
            change.Property == BoundsProperty)
        {
            Refresh();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopSweep();
        base.OnDetachedFromVisualTree(e);
    }

    private void Refresh()
    {
        double width = Bounds.Width;
        bool sweeping = IsIndeterminate && !IsComplete;

        ShowSweep = sweeping;
        ShowFill = !sweeping;
        SweepWidth = Math.Max(0, width * SweepFraction);
        FillWidth = IsComplete ? width : width * Fraction();

        if (sweeping)
            EnsureSweep(width);
        else
            StopSweep();
    }

    /// <summary>
    /// Refresh runs on every Value and Bounds change. Restarting the animation there
    /// would reset it to frame zero each tick, so the sweep would never travel.
    /// </summary>
    private void EnsureSweep(double width)
    {
        if (_sweepCancellation != null && Math.Abs(_runningSweepWidth - width) < 0.5)
            return;

        StartSweep(width);
    }

    private double Fraction()
    {
        double span = Maximum - Minimum;
        if (double.IsNaN(span) || span <= 0)
            return 0;

        return Math.Clamp((Value - Minimum) / span, 0, 1);
    }

    private void StartSweep(double width)
    {
        StopSweep();

        if (_sweep == null || width <= 0)
            return;

        double sweepWidth = width * SweepFraction;
        Animation animation = new()
        {
            Duration = TimeSpan.FromSeconds(1.7),
            IterationCount = IterationCount.Infinite,
            Easing = new SplineEasing(0.5, 0, 0.5, 1),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(TranslateTransform.XProperty, sweepWidth * -1.2) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(TranslateTransform.XProperty, sweepWidth * 3.2) },
                },
            },
        };

        _sweepCancellation = new CancellationTokenSource();
        _runningSweepWidth = width;
        _ = animation.RunAsync(_sweep, _sweepCancellation.Token);
    }

    private void StopSweep()
    {
        _sweepCancellation?.Cancel();
        _sweepCancellation?.Dispose();
        _sweepCancellation = null;
        _runningSweepWidth = double.NaN;
    }
}
