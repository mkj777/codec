using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec.ViewModels
{
    /// <summary>
    /// The developer corner, shown in Settings only while <see cref="IsDebugMode"/> holds.
    ///
    /// The WinUI build carried these behind a Debug flyout in the sidebar and they went
    /// missing in the port. They keep the same three jobs: dump what Codec knows about the
    /// library, resolve the ids for one executable, and start Steam the quiet way. Each answers
    /// through the notice pill rather than a dialog, because v2 has no dialogs.
    /// </summary>
    public partial class MainViewModel
    {
        private static readonly JsonSerializerOptions DebugDumpOptions = new() { WriteIndented = true };

        /// <summary>
        /// Writes everything Codec holds about the library to a file and opens it.
        ///
        /// The WinUI version built a nine hundred pixel wall of expanders on screen. A file is
        /// the same information, searchable, and does not have to be a surface Codec designs.
        /// </summary>
        [RelayCommand]
        private Task DebugDumpDataAsync() => RunExclusiveAsync(async () =>
        {
            try
            {
                string directory = _services.LibraryStorage.EnsureStorageInitialized();
                string path = Path.Combine(directory, $"debug-{DateTime.Now:yyyyMMdd-HHmmss}.json");

                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Games.ToList(), DebugDumpOptions))
                    .ConfigureAwait(true);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[Debug] dump failed: {exception}");
                ShowActionResult("Debug dump failed", exception.Message);
            }
        });

        /// <summary>Resolves the Steam id and best name Codec would pick for one executable.</summary>
        [RelayCommand]
        private Task DebugCheckIdsAsync() => RunExclusiveAsync(async () =>
        {
            string? executablePath = await _filePicker.PickExecutableAsync();
            if (executablePath == null)
                return;

            try
            {
                (int? steamId, string? steamName) = await _services.GameName.FindGameIdsAsync(executablePath);
                string name = steamName ?? _services.GameName.GetBestName(executablePath) ?? "Unknown";
                string id = steamId.HasValue ? $"Steam id {steamId.Value}" : "no Steam id";

                ShowActionResult("Check IDs", $"{name}: {id}");
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[Debug] id lookup failed: {exception}");
                ShowActionResult("Check IDs failed", exception.Message);
            }
        });

        /// <summary>Starts the Steam client with -silent, the way a launch would.</summary>
        [RelayCommand]
        private void DebugLaunchSteamSilent()
        {
            string? error = TryLaunchSteamSilent();
            ShowActionResult(
                error == null ? "Steam started quietly" : "Steam did not start",
                error);
        }
    }
}
