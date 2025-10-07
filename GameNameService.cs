using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec
{
    public static class GameNameService
    {
        private record SteamApp(int AppId, string Name);

        private static readonly HttpClient _httpClient = new();
        private const string SteamApiUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
        private const string SteamDetailsUrl = "https://store.steampowered.com/api/appdetails?appids=";
        private const string RawgApiUrl = "https://codec-api-proxy.vercel.app/api/search";

        private static List<SteamApp>? _cachedSteamApps;
        private static DateTime _cacheTimestamp = DateTime.MinValue;
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(24);

        private static readonly HashSet<string> DeprioritizedTerms = new(StringComparer.OrdinalIgnoreCase)
        {
            "win64", "win32", "x64", "x86", "bin", "binaries", "game", "data", "content",
            "win64-shipping", "win32-shipping", "shipping", "launcher", "bootstrap", "UE4", "UE5"
        };

        private static class NativeMethods
        {
            [DllImport("version.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern int GetFileVersionInfoSize(string lptstrFilename, out int lpdwHandle);

            [DllImport("version.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool GetFileVersionInfo(string lptstrFilename, int dwHandle, int dwLen, IntPtr lpData);

            [DllImport("version.dll", CharSet = CharSet.Unicode)]
            public static extern bool VerQueryValue(IntPtr pBlock, string lpSubBlock, out IntPtr lplpBuffer, out uint puLen);
        }

        private static string? GetVersionInfoValue(string path, string valueKey)
        {
            if (!File.Exists(path)) return null;

            int handle = 0;
            int size = NativeMethods.GetFileVersionInfoSize(path, out handle);
            if (size == 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!NativeMethods.GetFileVersionInfo(path, 0, size, buffer)) return null;

                if (NativeMethods.VerQueryValue(buffer, @"\VarFileInfo\Translation", out IntPtr transPtr, out uint transLen) && transLen > 0)
                {
                    int lang = Marshal.ReadInt16(transPtr);
                    int codePage = Marshal.ReadInt16(transPtr, 2);
                    string subBlock = $"\\StringFileInfo\\{lang:X4}{codePage:X4}\\{valueKey}";

                    if (NativeMethods.VerQueryValue(buffer, subBlock, out IntPtr valuePtr, out uint valueLen) && valueLen > 0)
                    {
                        return Marshal.PtrToStringUni(valuePtr);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return null;
        }

        public static async Task<(int? steamId, int? rawgId)> FindGameIdsAsync(string exePath)
        {
            int? steamId = await FindSteamIdAsync(exePath);
            int? rawgId = null;

            if (steamId.HasValue)
            {
                string? steamGameName = await GetSteamGameNameAsync(steamId.Value);
                if (!string.IsNullOrWhiteSpace(steamGameName))
                {
                    Debug.WriteLine($"Using Steam name for RAWG search: {steamGameName}");
                    rawgId = await FindRawgIdByNameAsync(steamGameName);
                }
            }

            if (!rawgId.HasValue)
            {
                Debug.WriteLine("No Steam ID or RAWG lookup failed, using EXE-based names...");
                rawgId = await FindRawgIdAsync(exePath);
            }

            return (steamId, rawgId);
        }

        private static async Task<string?> GetSteamGameNameAsync(int steamId)
        {
            try
            {
                string url = $"{SteamDetailsUrl}{steamId}";
                var response = await _httpClient.GetStringAsync(url);
                using var jsonDoc = JsonDocument.Parse(response);

                if (jsonDoc.RootElement.TryGetProperty(steamId.ToString(), out var appData) &&
                    appData.TryGetProperty("success", out var success) && success.GetBoolean() &&
                    appData.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("name", out var nameProperty))
                {
                    return nameProperty.GetString()?.Trim();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching Steam details: {ex.Message}");
            }
            return null;
        }

        private static async Task<int?> FindSteamIdAsync(string exePath)
        {
            int? steamAppId = GetSteamAppIdFromFile(exePath);
            if (steamAppId.HasValue) return steamAppId;

            var possibleNames = GetPrioritizedNames(exePath);
            if (!possibleNames.Any()) return null;

            try
            {
                var apps = await GetSteamAppsAsync();
                if (apps == null) return null;

                foreach (var name in possibleNames)
                {
                    Debug.WriteLine($"--- Searching with priority name: '{name}' ---");
                    var match = FindBestMatchForName(name, apps);
                    if (match.HasValue)
                    {
                        Debug.WriteLine($"✓ Final match found: {match.Value.id} for '{match.Value.name}' with score {match.Value.score:P}");
                        return match.Value.id;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Steam API error: {ex.Message}");
            }

            return null;
        }

        private static (int id, string name, double score)? FindBestMatchForName(string gameName, List<SteamApp> apps)
        {
            var searchWords = gameName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var potentialMatches = apps.Where(app =>
                app.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase) ||
                searchWords.Any(word => app.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            if (!potentialMatches.Any()) return null;

            var scoredMatches = potentialMatches.Select(app => (
                id: app.AppId,
                name: app.Name,
                score: CalculateSimilarity(gameName, app.Name)
            )).Where(m => m.score >= 0.7).ToList();

            if (!scoredMatches.Any()) return null;

            return scoredMatches.OrderByDescending(m => m.score).First();
        }

        private static List<string> GetPrioritizedNames(string exePath)
        {
            string? productName = GetVersionInfoValue(exePath, "ProductName");
            string? fileDescription = GetVersionInfoValue(exePath, "FileDescription");

            if (string.IsNullOrEmpty(productName) && string.IsNullOrEmpty(fileDescription))
            {
                string? muiPath = FindMuiFile(exePath);
                if (muiPath != null)
                {
                    productName = GetVersionInfoValue(muiPath, "ProductName");
                    fileDescription = GetVersionInfoValue(muiPath, "FileDescription");
                }
            }

            var names = new List<string?>
            {
                productName,
                fileDescription,
                new DirectoryInfo(Path.GetDirectoryName(exePath)!).Name,
                Path.GetFileNameWithoutExtension(exePath)
            };

            var distinctNames = names.Where(n => !string.IsNullOrWhiteSpace(n))
                                     .Select(n => n!.Trim())
                                     .Where(n => n.Length >= 3)
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(n => DeprioritizedTerms.Any(term => n.Contains(term, StringComparison.OrdinalIgnoreCase)))
                                     .ToList();

            Debug.WriteLine("Name priorities determined:");
            distinctNames.ForEach(name => Debug.WriteLine($"  - {name}"));

            return distinctNames;
        }

        private static string? FindMuiFile(string exePath)
        {
            string? dir = Path.GetDirectoryName(exePath);
            if (dir == null) return null;

            string baseName = Path.GetFileName(exePath);
            var culture = System.Globalization.CultureInfo.CurrentUICulture;

            string specificCulturePath = Path.Combine(dir, culture.Name, $"{baseName}.mui");
            if (File.Exists(specificCulturePath)) return specificCulturePath;

            if (culture.Parent != null && !string.IsNullOrEmpty(culture.Parent.Name))
            {
                string parentCulturePath = Path.Combine(dir, culture.Parent.Name, $"{baseName}.mui");
                if (File.Exists(parentCulturePath)) return parentCulturePath;
            }

            return null;
        }

        public static string? GetBestName(string exePath)
        {
            return GetPrioritizedNames(exePath).FirstOrDefault() ?? Path.GetFileNameWithoutExtension(exePath);
        }

        private static async ValueTask<List<SteamApp>?> GetSteamAppsAsync()
        {
            if (_cachedSteamApps != null && DateTime.UtcNow - _cacheTimestamp < CacheExpiration)
            {
                return _cachedSteamApps;
            }

            try
            {
                var response = await _httpClient.GetStringAsync(SteamApiUrl);
                var jsonDoc = JsonDocument.Parse(response);
                _cachedSteamApps = jsonDoc.RootElement
                                        .GetProperty("applist")
                                        .GetProperty("apps")
                                        .EnumerateArray()
                                        .Select(app => new SteamApp(
                                            app.GetProperty("appid").GetInt32(),
                                            app.GetProperty("name").GetString() ?? ""
                                        ))
                                        .Where(app => !string.IsNullOrWhiteSpace(app.Name))
                                        .ToList();
                _cacheTimestamp = DateTime.UtcNow;
                Debug.WriteLine($"Cached {_cachedSteamApps.Count} Steam apps.");
                return _cachedSteamApps;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Steam API error during app list fetch: {ex.Message}");
                return null;
            }
        }

        private static double CalculateSimilarity(string source, string target)
        {
            int distance = LevenshteinDistance(source.ToLowerInvariant(), target.ToLowerInvariant());
            int maxLength = Math.Max(source.Length, target.Length);
            return maxLength > 0 ? 1.0 - ((double)distance / maxLength) : 0.0;
        }

        private static int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
            if (string.IsNullOrEmpty(target)) return source.Length;

            var d = new int[source.Length + 1, target.Length + 1];
            for (int i = 0; i <= source.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= target.Length; j++) d[0, j] = j;

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[source.Length, target.Length];
        }

        private static async Task<int?> FindRawgIdAsync(string exePath)
        {
            string? bestName = GetBestName(exePath);
            if (string.IsNullOrWhiteSpace(bestName)) return null;
            return await FindRawgIdByNameAsync(bestName);
        }

        private static async Task<int?> FindRawgIdByNameAsync(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return null;
            try
            {
                string url = $"{RawgApiUrl}?term={Uri.EscapeDataString(gameName)}";
                var response = await _httpClient.GetStringAsync(url);
                using var jsonDoc = JsonDocument.Parse(response);
                if (jsonDoc.RootElement.TryGetProperty("results", out var results))
                {
                    var firstResult = results.EnumerateArray().FirstOrDefault();
                    if (firstResult.ValueKind != JsonValueKind.Undefined && firstResult.TryGetProperty("id", out var idProperty))
                    {
                        return idProperty.GetInt32();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RAWG API error: {ex.Message}");
            }
            return null;
        }

        private static int? GetSteamAppIdFromFile(string exePath)
        {
            try
            {
                string? gameDir = Path.GetDirectoryName(exePath);
                if (gameDir == null) return null;
                string filePath = Path.Combine(gameDir, "steam_appid.txt");
                if (File.Exists(filePath))
                {
                    if (int.TryParse(File.ReadAllText(filePath).Trim(), out int id))
                    {
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
    }
}
