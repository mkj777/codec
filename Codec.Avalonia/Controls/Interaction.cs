using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Codec.Controls;

/// <summary>
/// Two small behaviours that give styles the pointer state they cannot reach on their own.
///
/// Avalonia has no ancestor selector, so a press on the cover hitbox cannot restyle the card
/// that contains it. <see cref="PressClassProperty"/> puts the state on the card instead.
/// <see cref="PulseOnClickProperty"/> restores the click overshoot the WinUI hero buttons had,
/// which no pseudo-class expresses: <c>:pressed</c> is a state, and a pulse is an event.
/// </summary>
public static class Interaction
{
    private const string PressedClass = "pressed-in";
    private const string PulseClass = "pulse";

    private static readonly TimeSpan PulseDuration = TimeSpan.FromMilliseconds(210);

    /// <summary>Mirrors the pointer's pressed state onto this control as <c>pressed-in</c>.</summary>
    public static readonly AttachedProperty<bool> PressClassProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("PressClass", typeof(Interaction));

    /// <summary>Adds <c>pulse</c> for the length of one click animation.</summary>
    public static readonly AttachedProperty<bool> PulseOnClickProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("PulseOnClick", typeof(Interaction));

    private static readonly AttachedProperty<CancellationTokenSource?> PulseTokenProperty =
        AvaloniaProperty.RegisterAttached<Button, CancellationTokenSource?>("PulseToken", typeof(Interaction));

    static Interaction()
    {
        PressClassProperty.Changed.AddClassHandler<Control, bool>((control, e) =>
        {
            if (e.NewValue.GetValueOrDefault())
                AttachPressTracking(control);
            else
                DetachPressTracking(control);
        });

        PulseOnClickProperty.Changed.AddClassHandler<Button, bool>((button, e) =>
        {
            button.Click -= OnPulseClick;
            if (e.NewValue.GetValueOrDefault())
                button.Click += OnPulseClick;
        });
    }

    public static void SetPressClass(Control target, bool value) => target.SetValue(PressClassProperty, value);

    public static bool GetPressClass(Control target) => target.GetValue(PressClassProperty);

    public static void SetPulseOnClick(Button target, bool value) => target.SetValue(PulseOnClickProperty, value);

    public static bool GetPulseOnClick(Button target) => target.GetValue(PulseOnClickProperty);

    private static void AttachPressTracking(Control control)
    {
        DetachPressTracking(control);

        // The button inside marks the press handled on the way up, so the card only sees it
        // on the way down.
        control.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        control.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        control.AddHandler(InputElement.PointerCaptureLostEvent, OnReleased);
        control.AddHandler(InputElement.PointerExitedEvent, OnReleased);
    }

    private static void DetachPressTracking(Control control)
    {
        control.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        control.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        control.RemoveHandler(InputElement.PointerCaptureLostEvent, OnReleased);
        control.RemoveHandler(InputElement.PointerExitedEvent, OnReleased);
        control.Classes.Remove(PressedClass);
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            control.Classes.Add(PressedClass);
    }

    private static void OnReleased(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
            control.Classes.Remove(PressedClass);
    }

    private static void OnPulseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        if (button.GetValue(PulseTokenProperty) is { } pending)
        {
            button.SetValue(PulseTokenProperty, (CancellationTokenSource?)null);
            pending.Cancel();
        }

        // Removing first means a second click restarts the animation instead of being
        // swallowed by a class that is already present.
        button.Classes.Remove(PulseClass);
        button.Classes.Add(PulseClass);

        var cancellation = new CancellationTokenSource();
        button.SetValue(PulseTokenProperty, cancellation);
        _ = EndPulseAsync(button, cancellation);
    }

    private static async Task EndPulseAsync(Button button, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(PulseDuration, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        button.Classes.Remove(PulseClass);

        if (ReferenceEquals(button.GetValue(PulseTokenProperty), cancellation))
            button.SetValue(PulseTokenProperty, (CancellationTokenSource?)null);

        cancellation.Dispose();
    }
}
