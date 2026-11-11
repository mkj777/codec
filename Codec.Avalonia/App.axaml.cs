using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Codec.Infrastructure;
using Codec.Services;
using Codec.ViewModels;
using Codec.Views;

namespace Codec;

public sealed partial class App : Application
{
    public static ServiceHost Services { get; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Grayscale antialiasing, the way the WinUI build drew Nunito. Subpixel rendering left
        // colour fringes and thinner, harder stems. Tooltips and popups are top levels of their
        // own and inherit nothing from the window, so every top level gets it as its template
        // is applied, which is before its first frame.
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TopLevel>(
            (topLevel, _) => TextOptions.SetTextRenderingMode(topLevel, TextRenderingMode.Antialias));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow();

            // Placement has to be applied before the window is shown, or it opens at the
            // default size and visibly jumps. That is why AppSettingsService has a sync load.
            WindowChrome.RestorePlacement(window, Services.AppSettings.Load());

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
