using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Codec.Services.Storage;

namespace Codec.Infrastructure;

/// <summary>
/// The parts of looking like a real Windows application that live below the styling layer:
/// where the window opens, whether its corners are round, and what colour its frame is.
///
/// Avalonia draws the client area; the frame around it still belongs to DWM, and DWM has to be
/// told. Left alone, a window with an extended client area keeps the default light border and
/// square corners, which is the single clearest way to look like a program rather than an app.
/// </summary>
public static class WindowChrome
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmTransitionsForceDisabled = 3;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;

    /// <summary>DWMWCP_ROUND. Windows 11 only; earlier versions ignore the attribute.</summary>
    private const int CornerPreferenceRound = 2;

    /// <summary>A warm line rather than the default grey, in DWM's 0x00BBGGRR order.</summary>
    private const uint FrameColor = 0x00201D18;

    /// <summary>Anything smaller than this is treated as a corrupt or stale saved size.</summary>
    private const double MinimumRestorableSize = 400;

    /// <summary>
    /// Puts the window back where it was left, if that place still exists. A monitor can be
    /// unplugged between runs, so the saved position is only honoured when it still lands on a
    /// screen; otherwise the window falls back to opening centred.
    /// </summary>
    public static void RestorePlacement(Window window, AppSettings settings)
    {
        if (settings.WindowWidth is > MinimumRestorableSize and { } width &&
            settings.WindowHeight is > MinimumRestorableSize and { } height)
        {
            window.Width = width;
            window.Height = height;
        }

        if (settings.WindowX is { } x && settings.WindowY is { } y && IsOnAScreen(window, x, y))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(x, y);
        }

        if (settings.WindowMaximized)
            window.WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Applies the frame attributes DWM owns. Every call is a no-op on Windows 10 and earlier,
    /// where the attributes are simply unknown, so no version check is needed.
    /// </summary>
    public static void ApplyNativeFrame(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero)
            return;

        // A window that extends its client area can end up with the frame animation switched
        // off, which makes minimize and restore snap. Ask for it back explicitly.
        Set(handle.Handle, DwmTransitionsForceDisabled, 0);
        Set(handle.Handle, DwmUseImmersiveDarkMode, 1);
        Set(handle.Handle, DwmWindowCornerPreference, CornerPreferenceRound);
        Set(handle.Handle, DwmBorderColor, unchecked((int)FrameColor));
    }

    private static void Set(IntPtr window, int attribute, int value)
    {
        try
        {
            _ = DwmSetWindowAttribute(window, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // No desktop composition. Nothing to configure.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static bool IsOnAScreen(Window window, int x, int y)
    {
        IReadOnlyList<Screen>? screens = window.Screens?.All;
        if (screens is null || screens.Count == 0)
            return false;

        // The titlebar has to be reachable, not merely some corner of the window, so the saved
        // origin itself must land inside a screen's working area.
        foreach (Screen screen in screens)
        {
            if (screen.WorkingArea.Contains(new PixelPoint(x, y)))
                return true;
        }

        return false;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
