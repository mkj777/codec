using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Codec.Controls;

/// <summary>
/// Shows and hides a control through a class rather than through <see cref="Visual.IsVisible"/>,
/// so its entrance actually runs every time.
///
/// Avalonia starts a style animation when the selector becomes active. A static
/// <c>Classes</c> value activates once, when the control attaches, and
/// <c>IsVisible="False"</c> does not detach anything. Codec's <c>rise</c>, <c>fade</c> and
/// <c>breathe</c> animations were all written that way, so they played invisibly during
/// window load and never again.
///
/// Opening adds the <c>revealed</c> class, which the <c>.reveal</c> styles animate towards.
/// Closing removes it, waits for the exit to finish, and only then collapses the control, so
/// the leaving half is visible too.
/// </summary>
public static class Reveal
{
    private const string RevealedClass = "revealed";

    public static readonly AttachedProperty<bool> IsOpenProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsOpen", typeof(Reveal));

    /// <summary>
    /// How long the control stays in the tree after closing. Keep this at or above the
    /// longest transition on the matching <c>.reveal</c> style, or the control vanishes
    /// mid-exit.
    /// </summary>
    public static readonly AttachedProperty<TimeSpan> ExitDurationProperty =
        AvaloniaProperty.RegisterAttached<Control, TimeSpan>(
            "ExitDuration", typeof(Reveal), TimeSpan.FromMilliseconds(220));

    /// <summary>Cancels a pending collapse when the control is reopened mid-exit.</summary>
    private static readonly AttachedProperty<CancellationTokenSource?> ExitTokenProperty =
        AvaloniaProperty.RegisterAttached<Control, CancellationTokenSource?>("ExitToken", typeof(Reveal));

    static Reveal()
    {
        IsOpenProperty.Changed.AddClassHandler<Control, bool>((control, e) =>
        {
            if (e.NewValue.GetValueOrDefault())
                Open(control);
            else
                Close(control);
        });
    }

    public static void SetIsOpen(Control target, bool value) => target.SetValue(IsOpenProperty, value);

    public static bool GetIsOpen(Control target) => target.GetValue(IsOpenProperty);

    public static void SetExitDuration(Control target, TimeSpan value) => target.SetValue(ExitDurationProperty, value);

    public static TimeSpan GetExitDuration(Control target) => target.GetValue(ExitDurationProperty);

    private static void Open(Control control)
    {
        CancelPendingExit(control);
        control.IsVisible = true;

        // The control has to exist at its resting values before the class flips, or the
        // transition has nothing to run from.
        Dispatcher.UIThread.Post(() =>
        {
            if (GetIsOpen(control))
                control.Classes.Add(RevealedClass);
        }, DispatcherPriority.Background);
    }

    private static void Close(Control control)
    {
        CancelPendingExit(control);
        control.Classes.Remove(RevealedClass);

        TimeSpan exit = GetExitDuration(control);
        if (exit <= TimeSpan.Zero)
        {
            control.IsVisible = false;
            return;
        }

        var cancellation = new CancellationTokenSource();
        control.SetValue(ExitTokenProperty, cancellation);
        _ = CollapseAfterExitAsync(control, exit, cancellation);
    }

    private static async Task CollapseAfterExitAsync(Control control, TimeSpan exit, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(exit, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!GetIsOpen(control))
            control.IsVisible = false;

        if (ReferenceEquals(control.GetValue(ExitTokenProperty), cancellation))
            control.SetValue(ExitTokenProperty, (CancellationTokenSource?)null);

        cancellation.Dispose();
    }

    private static void CancelPendingExit(Control control)
    {
        if (control.GetValue(ExitTokenProperty) is not { } pending)
            return;

        control.SetValue(ExitTokenProperty, (CancellationTokenSource?)null);
        pending.Cancel();
    }
}
