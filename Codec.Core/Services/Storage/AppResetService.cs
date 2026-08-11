using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Codec.Services.Storage;

public sealed class AppResetService
{
    internal static void DeleteAppData()
    {
        string targetDirectory = GetValidatedAppDataDirectory();
        if (Directory.Exists(targetDirectory))
            Directory.Delete(targetDirectory, recursive: true);
    }

    public void StartFullResetAndRestart()
    {
        string targetDirectory = GetValidatedAppDataDirectory();
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Codec could not locate its executable for restart.");
        executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Codec could not locate its executable for restart.", executablePath);

        string powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        if (!File.Exists(powerShellPath))
            throw new FileNotFoundException("Windows PowerShell is required to complete the reset.", powerShellPath);

        string escapedTarget = targetDirectory.Replace("'", "''", StringComparison.Ordinal);
        string escapedExecutable = executablePath.Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            $target = '{{escapedTarget}}'
            $executable = '{{escapedExecutable}}'
            Wait-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
            while (Test-Path -LiteralPath $target) {
                Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $target) { Start-Sleep -Milliseconds 250 }
            }
            Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable)
            """;
        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);

        using Process helper = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Codec could not start the reset helper.");

        Environment.Exit(0);
    }

    internal static string GetValidatedAppDataDirectory()
    {
        string localAppData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        string expected = Path.GetFullPath(Path.Combine(localAppData, LibraryStorageService.AppDataFolderName));
        string? parent = Directory.GetParent(expected)?.FullName;

        if (!string.Equals(parent, localAppData, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(expected), LibraryStorageService.AppDataFolderName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codec refused to reset because its data path was not valid.");
        }

        return expected;
    }
}
