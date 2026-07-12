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
        public bool ScanOnStartup { get; set; } = true;
        public bool LaunchSteamSilent { get; set; }
        public string? SteamClientPath { get; set; }
        public string? EpicLauncherPath { get; set; }
        public int SelectedSortIndex { get; set; }
        public bool IsSidebarCollapsed { get; set; }
        public string? SteamAccountName { get; set; }
        public DateTime? LastSteamSyncUtc { get; set; }
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
