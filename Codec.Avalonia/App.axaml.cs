using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Codec.Infrastructure;
using Codec.Services;
using Codec.ViewModels;
using Codec.Views;

namespace Codec;

public sealed partial class App : Application
{
    public static ServiceHost Services { get; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow();
            var dispatcher = new AvaloniaUiDispatcher();
            var filePicker = new AvaloniaFilePickerService(() => window);
            var windowService = new AvaloniaWindowService(desktop, () => window);
            var trayService = new TrayService(
                Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"),
                windowService.ShowMainWindow,
                windowService.CloseApplication);
            window.DataContext = new MainViewModel(Services, dispatcher, filePicker, windowService);
            window.ConfigureLifecycle(windowService, trayService);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
