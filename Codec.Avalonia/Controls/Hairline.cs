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

    private const string SweepingClass = "sweeping";

    private Control? _sweep;
    private double _runningSweepWidth = double.NaN;

    public static readonly StyledProperty<double> SweepProgressProperty =
        AvaloniaProperty.Register<Hairline, double>(nameof(SweepProgress));

    public Hairline()
    {
        Styles.Add(CreateSweepStyle());
    }

    /// <summary>Where the sweep is on its way across, 0 to 1. Driven by the sweep animation.</summary>
    public double SweepProgress
    {
        get => GetValue(SweepProgressProperty);
        set => SetValue(SweepProgressProperty, value);
    }

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

        if (change.Property == SweepProgressProperty)
        {
            PositionSweep(change.GetNewValue<double>());
            return;
        }

        if (change.Property == ValueProperty ||
            change.Property == MinimumProperty ||
            change.Property == MaximumProperty ||
            change.Property == IsIndeterminateProperty ||
            change.Property == IsCompleteProperty ||
            change.Property == BoundsProperty ||
            change.Property == OpacityProperty)
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

        // A line held in place at no opacity is laid out and therefore "visible" to the
        // animation, which would tick for nobody. It starts again as the fade begins.
        bool running = sweeping && Opacity > 0;

        ShowSweep = sweeping;
        ShowFill = !sweeping;
        SweepWidth = Math.Max(0, width * SweepFraction);
        FillWidth = IsComplete ? width : width * Fraction();

        if (running)
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
        if (Classes.Contains(SweepingClass) && Math.Abs(_runningSweepWidth - width) < 0.5)
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

    /// <summary>
    /// The sweep is a style animation on a normalized progress, switched on by a class, the
    /// same way Motion.axaml's "breathe" runs. Avalonia only allows an endless animation that
    /// way: RunAsync refuses them, and because that task was discarded the sweep silently
    /// never moved while the refusal filled crash.log.
    /// </summary>
    private static Style CreateSweepStyle()
    {
        Animation animation = new()
        {
            Duration = TimeSpan.FromSeconds(1.7),
            IterationCount = IterationCount.Infinite,
            // Reveal collapses the panel around the line, not the line itself, and an endless
            // sweep nobody sees would otherwise tick and commit every frame for the session.
            PlaybackBehavior = PlaybackBehavior.OnlyIfVisible,
            Easing = new SplineEasing(0.5, 0, 0.5, 1),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(SweepProgressProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(SweepProgressProperty, 1d) } },
            },
        };

        return new Style(x => x.OfType<Hairline>().Class(SweepingClass)) { Animations = { animation } };
    }

    private void StartSweep(double width)
    {
        StopSweep();

        if (_sweep == null || width <= 0)
            return;

        _runningSweepWidth = width;
        _sweep.RenderTransform = new TranslateTransform();
        Classes.Add(SweepingClass);
    }

    private void PositionSweep(double progress)
    {
        if (_sweep?.RenderTransform is not TranslateTransform transform || double.IsNaN(_runningSweepWidth))
            return;

        // From fully off the left edge to fully past the right one, for a line that is
        // SweepFraction of the track wide.
        double sweepWidth = _runningSweepWidth * SweepFraction;
        transform.X = sweepWidth * (-1.2 + progress * 4.4);
    }

    private void StopSweep()
    {
        Classes.Remove(SweepingClass);
        _runningSweepWidth = double.NaN;
    }
}
