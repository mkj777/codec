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

        /// <summary>
        /// Downloads the Steam library capsule (600x900) for a given Steam App ID.
        /// Returns a file URI string usable by Image.Source if successful, otherwise null.
        /// </summary>
        public static async Task<string?> DownloadSteamLibraryCoverAsync(int steamId)
        {
            try
            {
                string url = $"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/library_600x900_2x.jpg";
                string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codec", "Images", "Capsules");
                Directory.CreateDirectory(baseDir);
                string filePath = Path.Combine(baseDir, $"steam_{steamId}_library_600x900_2x.jpg");

                if (!File.Exists(filePath))
                {
                    using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                    {
                        Debug.WriteLine($"Steam cover not available for {steamId} (Status {(int)response.StatusCode})");
                        return null;
                    }

                    await using var remoteStream = await response.Content.ReadAsStreamAsync();
                    await using var localStream = File.Create(filePath);
                    await remoteStream.CopyToAsync(localStream);
                }

                // Return file URI for binding
                return new Uri(filePath).AbsoluteUri;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Cover download failed for {steamId}: {ex.Message}");
                return null;
            }
        }
    }
}
