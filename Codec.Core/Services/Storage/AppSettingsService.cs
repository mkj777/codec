using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec.Services.Storage
{
    public class AppSettings
    {
        public bool OnboardingCompleted { get; set; }
        public bool ScanOnStartup { get; set; }
        public bool LaunchSteamSilent { get; set; }
        public bool? CloseToTray { get; set; }
        public string? SteamClientPath { get; set; }
        public string? EpicLauncherPath { get; set; }
        public int SelectedSortIndex { get; set; }
        public bool IsSidebarCollapsed { get; set; }
        public string? SteamAccountName { get; set; }
        public DateTime? LastSteamSyncUtc { get; set; }
        public ulong? SteamId64 { get; set; }
        public DateTime? SteamAchievementsRetryAfterUtc { get; set; }

        // Persisted so a sign-in that keeps failing across restarts still adds up; three in a
        // row sign Steam out. Any successful sync sets it back to zero.
        public int SteamBackgroundSyncFailures { get; set; }

        // Window placement. Position is in physical pixels because that is what the platform
        // reports; size stays in device-independent units so moving between monitors of
        // different scaling keeps the window the same apparent size.
        public int? WindowX { get; set; }
        public int? WindowY { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
    }

    public class AppSettingsService
    {
        private const string SettingsFileName = "settings.json";

        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
        private readonly JsonFileStore _store;

        /// <param name="baseDirectory">Where settings.json lives. Defaults to the per-user app data folder.</param>
        public AppSettingsService(string? baseDirectory = null)
        {
            _store = new JsonFileStore(Path.Combine(
                baseDirectory ?? LibraryStorageService.GetDefaultBaseDirectory(),
                SettingsFileName));
        }

        /// <summary>What the most recent load found on disk.</summary>
        public StorageLoadOutcome LastLoadOutcome => _store.LastLoadOutcome;

        /// <summary>The error of the most recent write, or null when it succeeded.</summary>
        public Exception? LastSaveError => _store.LastSaveError;

        /// <summary>Raised on a background thread when a settings write fails.</summary>
        public event EventHandler<Exception>? SaveFailed
        {
            add => _store.SaveFailed += value;
            remove => _store.SaveFailed -= value;
        }

        /// <summary>
        /// Synchronous load, for the one caller that needs settings before the first frame:
        /// window placement has to be applied before the window is shown or it visibly jumps.
        /// </summary>
        public AppSettings Load() => _store.Load<AppSettings>(_jsonOptions) ?? new AppSettings();

        public async Task<AppSettings> LoadAsync()
            => await Task.Run(Load).ConfigureAwait(false);

        /// <summary>
        /// Saves the settings. They are serialized before this returns, so the caller may keep
        /// changing the same instance; the disk write runs in the background and saves requested
        /// later always win. Never throws; false means this state did not reach disk.
        /// </summary>
        public Task<bool> SaveAsync(AppSettings settings)
        {
            byte[] content;
            try
            {
                content = JsonSerializer.SerializeToUtf8Bytes(settings, _jsonOptions);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to serialize app settings: {ex.Message}");
                return Task.FromResult(false);
            }

            return _store.WriteAsync(content);
        }

        /// <summary>Completes once every save requested so far has finished.</summary>
        public Task FlushAsync() => _store.FlushAsync();

        /// <summary>Blocks until pending saves finish or the timeout passes.</summary>
        public bool Flush(TimeSpan timeout) => _store.Flush(timeout);
    }
}
