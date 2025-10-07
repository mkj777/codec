using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Codec
{
    public static class GameNameService
    {
        private static readonly HttpClient _httpClient = new();
        private const string SteamApiUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
        private const string SteamDetailsUrl = "https://store.steampowered.com/api/appdetails?appids=";
        private const string RawgApiUrl = "https://codec-api-proxy.vercel.app/api/search";

        // Main entry point: tries Steam first, then uses Steam name or EXE name for RAWG
        public static async Task<(int? steamId, int? rawgId)> FindGameIdsAsync(string exePath)
        {
            int? steamId = await FindSteamIdAsync(exePath);
            int? rawgId = null;

            if (steamId.HasValue)
            {
                // Get game name from Steam Details API
                string steamGameName = await GetSteamGameNameAsync(steamId.Value);
                if (!string.IsNullOrWhiteSpace(steamGameName))
                {
                    Debug.WriteLine($"Using Steam name for RAWG search: {steamGameName}");
                    rawgId = await FindRawgIdByNameAsync(steamGameName);
                }
            }

            // Fallback to EXE-based names if no RAWG ID found yet
            if (!rawgId.HasValue)
            {
                Debug.WriteLine("No Steam ID or RAWG lookup failed, using EXE-based names...");
                rawgId = await FindRawgIdAsync(exePath);
            }

            return (steamId, rawgId);
        }

        // Get game name from Steam Store API using Steam ID
        private static async Task<string?> GetSteamGameNameAsync(int steamId)
        {
            try
            {
                string url = $"{SteamDetailsUrl}{steamId}";
                Debug.WriteLine($"Fetching Steam details for ID {steamId}");

                var response = await _httpClient.GetStringAsync(url);
                var jsonDoc = JsonDocument.Parse(response);

                // Steam API returns: { "appid": { "success": true, "data": { "name": "..." } } }
                if (jsonDoc.RootElement.TryGetProperty(steamId.ToString(), out var appData))
                {
                    if (appData.TryGetProperty("success", out var success) && success.GetBoolean())
                    {
                        if (appData.TryGetProperty("data", out var data) &&
                            data.TryGetProperty("name", out var nameProperty))
                        {
                            string name = nameProperty.GetString();
                            Debug.WriteLine($"Steam game name: {name}");
                            return name;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching Steam details: {ex.Message}");
            }

            return null;
        }

        // Search for Steam ID
        private static async Task<int?> FindSteamIdAsync(string exePath)
        {
            // Check steam_appid.txt first
            int? steamAppId = GetSteamAppIdFromFile(exePath);
            if (steamAppId.HasValue)
            {
                Debug.WriteLine($"Steam ID from file: {steamAppId.Value}");
                return steamAppId;
            }

            var possibleNames = GetPossibleNames(exePath);
            if (!possibleNames.Any()) return null;

            try
            {
                var response = await _httpClient.GetStringAsync(SteamApiUrl);
                var jsonDoc = JsonDocument.Parse(response);
                var apps = jsonDoc.RootElement.GetProperty("applist").GetProperty("apps").EnumerateArray().ToList();

                foreach (var gameName in possibleNames)
                {
                    string normalized = NormalizeName(gameName);
                    Debug.WriteLine($"Searching Steam for: {gameName}");

                    // Exact match
                    foreach (var app in apps)
                    {
                        string appName = app.GetProperty("name").GetString() ?? string.Empty;
                        if (NormalizeName(appName).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                        {
                            int id = app.GetProperty("appid").GetInt32();
                            Debug.WriteLine($"Steam ID found (exact): {id}");
                            return id;
                        }
                    }

                    // Partial match
                    foreach (var app in apps)
                    {
                        string appName = app.GetProperty("name").GetString() ?? string.Empty;
                        string normalizedApp = NormalizeName(appName);
                        if (normalizedApp.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains(normalizedApp, StringComparison.OrdinalIgnoreCase))
                        {
                            int id = app.GetProperty("appid").GetInt32();
                            Debug.WriteLine($"Steam ID found (partial): {id}");
                            return id;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Steam API error: {ex.Message}");
            }

            return null;
        }

        // Search for RAWG.io ID using EXE-based names
        private static async Task<int?> FindRawgIdAsync(string exePath)
        {
            string bestName = GetBestName(exePath);
            if (string.IsNullOrWhiteSpace(bestName)) return null;

            return await FindRawgIdByNameAsync(bestName);
        }

        // Search for RAWG.io ID by game name
        private static async Task<int?> FindRawgIdByNameAsync(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return null;

            try
            {
                string url = $"{RawgApiUrl}?term={Uri.EscapeDataString(gameName)}";
                Debug.WriteLine($"Searching RAWG.io for: {gameName}");

                var response = await _httpClient.GetStringAsync(url);
                var jsonDoc = JsonDocument.Parse(response);

                if (jsonDoc.RootElement.TryGetProperty("results", out var results))
                {
                    var firstResult = results.EnumerateArray().FirstOrDefault();
                    if (firstResult.ValueKind != JsonValueKind.Undefined &&
                        firstResult.TryGetProperty("id", out var idProperty))
                    {
                        int id = idProperty.GetInt32();
                        string name = firstResult.TryGetProperty("name", out var n) ? n.GetString() : "Unknown";
                        Debug.WriteLine($"RAWG ID found: {id} for '{name}'");
                        return id;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RAWG API error: {ex.Message}");
            }

            return null;
        }

        // Get all possible game names from various sources
        private static List<string> GetPossibleNames(string exePath)
        {
            var names = new List<string>();
            string gameDir = Path.GetDirectoryName(exePath);

            // Priority 1: Product name from EXE metadata
            var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(versionInfo.ProductName))
                names.Add(versionInfo.ProductName);

            // Priority 2: File description
            if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription) &&
                versionInfo.FileDescription != versionInfo.ProductName)
                names.Add(versionInfo.FileDescription);

            // Priority 3: Directory name
            names.Add(CleanName(new DirectoryInfo(gameDir).Name));

            // Priority 4: EXE filename
            names.Add(CleanName(Path.GetFileNameWithoutExtension(exePath)));

            // Priority 5: Parent directory name
            var parentDir = Directory.GetParent(gameDir);
            if (parentDir != null)
                names.Add(CleanName(parentDir.Name));

            return names.Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
        }

        // Get best single name
        public static string GetBestName(string exePath)
        {
            var names = GetPossibleNames(exePath);
            return names.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(exePath);
        }

        // Check for steam_appid.txt file
        private static int? GetSteamAppIdFromFile(string exePath)
        {
            try
            {
                string gameDir = Path.GetDirectoryName(exePath);
                string filePath = Path.Combine(gameDir, "steam_appid.txt");
                if (File.Exists(filePath))
                {
                    string content = File.ReadAllText(filePath).Trim();
                    if (int.TryParse(content, out int id))
                    {
                        Debug.WriteLine($"Found steam_appid.txt: {id}");
                        return id;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading steam_appid.txt: {ex.Message}");
            }
            return null;
        }

        // Clean name: remove underscores, apply title case, remove version numbers
        private static string CleanName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            string cleaned = Regex.Replace(name, @"\s*[\d\.]+\s*$", "");
            cleaned = Regex.Replace(cleaned, @"\s*-\s*Game\s*$", "", RegexOptions.IgnoreCase);
            cleaned = cleaned.Replace('_', ' ').Replace('-', ' ');
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            if (!string.IsNullOrWhiteSpace(cleaned))
                cleaned = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleaned.ToLower());

            return cleaned;
        }

        // Normalize name for comparison
        private static string NormalizeName(string name) =>
            name.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
    }
}
