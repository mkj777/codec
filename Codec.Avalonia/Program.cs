using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Threading;
using Codec.Services.Storage;
using Velopack;

namespace Codec;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Without this a crash is a silent disappearance: the process is a WinExe, so nothing
        // reaches a console and there is no window left to report into.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("unobserved task", e.Exception); e.SetObserved(); };
        Dispatcher.UIThread.UnhandledException += (_, e) => Log("dispatcher", e.Exception);

        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => AppResetService.DeleteAppData())
            .Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void Log(string source, Exception? error)
    {
        if (error is null)
            return;

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LibraryStorageService.AppDataFolderName);
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:u} [{source}] {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception writeFailure)
        {
            Debug.WriteLine($"Could not write the crash log: {writeFailure.Message}");
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
