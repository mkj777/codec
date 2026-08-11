using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using System.Diagnostics;

namespace Codec.Infrastructure;

public sealed class AvaloniaWindowService(
    IClassicDesktopStyleApplicationLifetime desktop,
    Func<Window?> getMainWindow) : IWindowService
{
    public bool IsShutdownRequested { get; private set; }

    public void HideMainWindow() => getMainWindow()?.Hide();

    public void ShowMainWindow()
    {
        Window? window = getMainWindow();
        if (window == null)
            return;

        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void CloseApplication()
    {
        if (IsShutdownRequested)
            return;

        IsShutdownRequested = true;
        desktop.Shutdown();
    }

    public Task OpenExternalUriAsync(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
