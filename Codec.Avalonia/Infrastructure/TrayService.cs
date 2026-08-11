using Avalonia.Controls;

namespace Codec.Infrastructure;

public sealed class TrayService : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private bool _disposed;

    public TrayService(string iconPath, Action open, Action exit)
    {
        var openItem = new NativeMenuItem("Open Codec");
        openItem.Click += (_, _) => open();
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => exit();

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconPath),
            ToolTipText = "Codec",
            Menu = menu,
            IsVisible = false
        };
        _trayIcon.Clicked += (_, _) => open();
    }

    public void Show() => _trayIcon.IsVisible = true;

    public void Hide() => _trayIcon.IsVisible = false;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }
}
