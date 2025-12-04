using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Codec.Services
{
    public static class GameAssetService
    {
        private static readonly HttpClient _httpClient = new();

        private static string GetCapsulesDir()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codec", "Images", "Capsules");
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
                    ($"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/library_600x900_2x.jpg", $"steam_{steamId}_library_600x900_2x.jpg"),
                    ($"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/library_600x900.jpg", $"steam_{steamId}_library_600x900.jpg"),
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
    }
}
