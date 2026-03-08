using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec.Services
{
    public static class GameAssetService
    {
        private static readonly HttpClient _httpClient = new();

        private static string GetCapsulesDir()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LibraryStorageService.AppDataFolderName, "Assets", "Capsules");
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        private static string GetGridDbDir()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LibraryStorageService.AppDataFolderName, "Assets", "GridDb");
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        /// <summary>
        /// Downloads the Steam library cover for a given Steam App ID.
        /// Attempts several known variants and returns the first successful local file URI.
        /// If force is true, existing local files are overwritten.
        /// </summary>
        public static async Task<string?> DownloadSteamLibraryCoverAsync(int steamId, bool force = false)
        {
            try
            {
                var variants = new (string Url, string FileName)[]
                {
                    ($"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/library_600x900.jpg", $"steam_{steamId}_library_600x900.jpg"),
                    ($"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/library_600x900_2x.jpg", $"steam_{steamId}_library_600x900_2x.jpg"),
                    ($"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/capsule_616x353.jpg", $"steam_{steamId}_capsule_616x353.jpg"),
                    ($"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/header.jpg", $"steam_{steamId}_header.jpg"),
                };

                string dir = GetCapsulesDir();

                foreach (var (url, fileName) in variants)
                {
                    string filePath = Path.Combine(dir, fileName);

                    if (File.Exists(filePath))
                    {
                        if (!force)
                        {
                            return new Uri(filePath).AbsoluteUri;
                        }
                        try { File.Delete(filePath); } catch (Exception delEx) { Debug.WriteLine($"Failed to delete old cover: {delEx.Message}"); }
                    }

                    using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                    {
                        Debug.WriteLine($"Cover variant not available for {steamId}: {url} -> {(int)response.StatusCode}");
                        continue;
                    }

                    await using var remoteStream = await response.Content.ReadAsStreamAsync();
                    await using var localStream = File.Create(filePath);
                    await remoteStream.CopyToAsync(localStream);

                    return new Uri(filePath).AbsoluteUri;
                }

                Debug.WriteLine($"No Steam cover found for {steamId} across known variants.");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Cover download failed for {steamId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Downloads the first available SteamGridDB grid for the given entry.
        /// </summary>
        public static async Task<string?> DownloadGridDbCoverAsync(int gridDbId, bool force = false)
        {
            try
            {
                string gridsUrl = $"https://codec-api-proxy.vercel.app/api/griddb/grids?id={gridDbId}";
                var response = await _httpClient.GetStringAsync(gridsUrl);
                using var doc = JsonDocument.Parse(response);

                if (!doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.GetArrayLength() == 0)
                {
                    return null;
                }

                var first = dataArray[0];
                if (!first.TryGetProperty("url", out var urlProp))
                {
                    return null;
                }

                string? gridUrl = urlProp.GetString();
                if (string.IsNullOrEmpty(gridUrl))
                {
                    return null;
                }

                int gridImageId = first.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;

                string extension = ".jpg";
                if (Uri.TryCreate(gridUrl, UriKind.Absolute, out var parsedUri))
                {
                    string pathExt = Path.GetExtension(parsedUri.AbsolutePath);
                    if (!string.IsNullOrEmpty(pathExt))
                    {
                        extension = pathExt;
                    }
                }

                string dir = GetGridDbDir();
                string fileName = gridImageId > 0 ? $"griddb_{gridDbId}_{gridImageId}{extension}" : $"griddb_{gridDbId}{extension}";
                string filePath = Path.Combine(dir, fileName);

                if (File.Exists(filePath) && !force)
                {
                    return new Uri(filePath).AbsoluteUri;
                }

                using var downloadResponse = await _httpClient.GetAsync(gridUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!downloadResponse.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"GridDB cover variant not available for {gridDbId}: {gridUrl} -> {(int)downloadResponse.StatusCode}");
                    return null;
                }

                await using var remoteStream = await downloadResponse.Content.ReadAsStreamAsync();
                await using var localStream = File.Create(filePath);
                await remoteStream.CopyToAsync(localStream);

                return new Uri(filePath).AbsoluteUri;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GridDB cover download failed for {gridDbId}: {ex.Message}");
                return null;
            }
        }
    }
}
