using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using System.Diagnostics;

namespace Codec.Infrastructure;

public sealed class AvaloniaWindowService(
    IClassicDesktopStyleApplicationLifetime desktop,
    Func<Window?> getMainWindow) : IWindowService
{
    // Bounded so a stuck disk cannot keep the process alive after Exit.
    private static readonly TimeSpan SaveFlushTimeout = TimeSpan.FromSeconds(5);

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

    public async void CloseApplication()
    {
        if (IsShutdownRequested)
            return;

        IsShutdownRequested = true;

        // Exit from the tray ends the process with the window hidden, so this is the last
        // point where saves still queued in the background can finish.
        try
        {
            await Task.WhenAll(App.Services.AppSettings.FlushAsync(), App.Services.LibraryStorage.FlushAsync())
                .WaitAsync(SaveFlushTimeout);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Shutdown] Pending saves did not finish: {ex.Message}");
        }

        desktop.Shutdown();
    }

    public Task OpenExternalUriAsync(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
