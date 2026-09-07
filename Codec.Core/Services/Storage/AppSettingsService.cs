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
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        private string GetPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LibraryStorageService.AppDataFolderName);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }

        /// <summary>
        /// Synchronous load, for the one caller that needs settings before the first frame:
        /// window placement has to be applied before the window is shown or it visibly jumps.
        /// </summary>
        public AppSettings Load()
        {
            try
            {
                string path = GetPath();
                if (!File.Exists(path))
                    return new AppSettings();
                using var fs = File.OpenRead(path);
                return JsonSerializer.Deserialize<AppSettings>(fs, _jsonOptions) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load app settings: {ex.Message}");
                return new AppSettings();
            }
        }

        public async Task<AppSettings> LoadAsync()
        {
            try
            {
                string path = GetPath();
                if (!File.Exists(path))
                    return new AppSettings();
                await using var fs = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<AppSettings>(fs, _jsonOptions) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load app settings: {ex.Message}");
                return new AppSettings();
            }
        }

        public async Task SaveAsync(AppSettings settings)
        {
            try
            {
                string path = GetPath();
                await using var fs = File.Create(path);
                await JsonSerializer.SerializeAsync(fs, settings, _jsonOptions);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save app settings: {ex.Message}");
            }
        }
    }
}
