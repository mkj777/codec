using Codec.Services.Storage;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using Codec.Services.Scanning;
using System.Collections.Concurrent;

namespace Codec.Services.Fetching
{
    public class GameAssetService
    {
        private readonly HttpClient _http = new();
        private readonly SteamKitService? _steamKit;
        private readonly ScanResourceLimiter? _resourceLimiter;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _assetGates = new(StringComparer.OrdinalIgnoreCase);

        public GameAssetService(SteamKitService? steamKit = null, ScanResourceLimiter? resourceLimiter = null)
        {
            _steamKit = steamKit;
            _resourceLimiter = resourceLimiter;
        }

        private Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead) =>
            _resourceLimiter is null
                ? _http.GetAsync(url, completionOption)
                : _resourceLimiter.RunNetworkAsync(ct => _http.GetAsync(url, completionOption, ct));

        private Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead) =>
            _resourceLimiter is null
                ? _http.GetAsync(url, completionOption)
                : _resourceLimiter.RunNetworkAsync(ct => _http.GetAsync(url, completionOption, ct));

        private Task<string> GetStringAsync(string url) =>
            _resourceLimiter is null
                ? _http.GetStringAsync(url)
                : _resourceLimiter.RunNetworkAsync(ct => _http.GetStringAsync(url, ct));

        private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption) =>
            _resourceLimiter is null
                ? _http.SendAsync(request, completionOption)
                : _resourceLimiter.RunNetworkAsync(ct => _http.SendAsync(request, completionOption, ct));

        private string GetCapsulesDir()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LibraryStorageService.AppDataFolderName, "Assets", "Capsules");
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        private string GetGridDbDir()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LibraryStorageService.AppDataFolderName, "Assets", "GridDb");
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        private string GetHeroesDir()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LibraryStorageService.AppDataFolderName, "Assets", "Heroes");
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        private string GetLogosDir()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LibraryStorageService.AppDataFolderName, "Assets", "Logos");
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        /// <summary>
        /// Downloads the Steam library cover for a given Steam App ID.
        /// Attempts several known variants and returns the first successful local file path.
        /// If force is true, existing local files are overwritten.
        /// </summary>
        public async Task<string?> DownloadSteamLibraryCoverAsync(int steamId, bool force = false)
        {
            var assetGate = _assetGates.GetOrAdd($"steam-cover:{steamId}", _ => new SemaphoreSlim(1, 1));
            await assetGate.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    string dir = GetCapsulesDir();

                // Preferred path: PICS-resolved library_capsule hash URL.
                if (_steamKit != null)
                {
                    var assets = await _steamKit.GetLibraryAssetsAsync((uint)steamId).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(assets?.CapsuleUrl) && !string.IsNullOrEmpty(assets.CapsuleHash))
                    {
                        string hashShort = assets.CapsuleHash.Length > 12 ? assets.CapsuleHash[..12] : assets.CapsuleHash;
                        string fileName = $"steam_{steamId}_library_capsule_{hashShort}.jpg";
                        string filePath = Path.Combine(dir, fileName);

                        if (File.Exists(filePath) && !force)
                        {
                            return filePath;
                        }

                        try
                        {
                            using var picsResponse = await GetAsync(assets.CapsuleUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                            if (picsResponse.IsSuccessStatusCode)
                            {
                                if (File.Exists(filePath)) { try { File.Delete(filePath); } catch { } }
                                await using var remote = await picsResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
                                await WriteStreamAtomicallyAsync(remote, filePath).ConfigureAwait(false);
                                return filePath;
                            }
                        }
                        catch (Exception picsEx)
                        {
                            Debug.WriteLine($"PICS capsule download failed for {steamId}: {picsEx.Message}");
                        }
                    }
                }

                var variants = new (string Url, string FileName)[]
                {
                    ($"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/library_600x900.jpg", $"steam_{steamId}_library_600x900.jpg"),
                    ($"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/library_600x900_2x.jpg", $"steam_{steamId}_library_600x900_2x.jpg"),
                    ($"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/capsule_616x353.jpg", $"steam_{steamId}_capsule_616x353.jpg"),
                    ($"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/header.jpg", $"steam_{steamId}_header.jpg"),
                };

                foreach (var (url, fileName) in variants)
                {
                    string filePath = Path.Combine(dir, fileName);

                    if (File.Exists(filePath))
                    {
                        if (!force)
                        {
                            return filePath;
                        }
                        try { File.Delete(filePath); } catch (Exception delEx) { Debug.WriteLine($"Failed to delete old cover: {delEx.Message}"); }
                    }

                    using var response = await GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                    {
                        Debug.WriteLine($"Cover variant not available for {steamId}: {url} -> {(int)response.StatusCode}");
                        continue;
                    }

                    await using var remoteStream = await response.Content.ReadAsStreamAsync();
                    await WriteStreamAtomicallyAsync(remoteStream, filePath).ConfigureAwait(false);

                    return filePath;
                }

                string? headerImageUrl = await FetchSteamHeaderImageUrlAsync(steamId).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(headerImageUrl))
                {
                    string? fallbackPath = await CacheImageAsync("Capsules", $"steam_{steamId}_header_image", headerImageUrl, force).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(fallbackPath))
                    {
                        return fallbackPath;
                    }
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
            finally
            {
                assetGate.Release();
            }
        }

        /// <summary>
        /// Downloads the first available SteamGridDB grid for the given entry.
        /// </summary>
        public async Task<string?> DownloadGridDbCoverAsync(int gridDbId, bool force = false)
        {
            var assetGate = _assetGates.GetOrAdd($"grid-cover:{gridDbId}", _ => new SemaphoreSlim(1, 1));
            await assetGate.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    string gridsUrl = $"https://codec-api-proxy.vercel.app/api/griddb/grids?id={gridDbId}";
                var response = await GetStringAsync(gridsUrl);
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
                    return filePath;
                }

                using var downloadResponse = await GetAsync(gridUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!downloadResponse.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"GridDB cover variant not available for {gridDbId}: {gridUrl} -> {(int)downloadResponse.StatusCode}");
                    return null;
                }

                await using var remoteStream = await downloadResponse.Content.ReadAsStreamAsync();
                await WriteStreamAtomicallyAsync(remoteStream, filePath).ConfigureAwait(false);

                return filePath;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GridDB cover download failed for {gridDbId}: {ex.Message}");
                    return null;
                }
            }
            finally
            {
                assetGate.Release();
            }
        }

        public async Task<string?> FetchSteamHeaderImageUrlAsync(int steamId)
        {
            try
            {
                string json = await GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={steamId}").ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement.GetProperty(steamId.ToString());
                if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
                    return null;
                if (!root.TryGetProperty("data", out var data))
                    return null;
                if (!data.TryGetProperty("header_image", out var headerProp))
                    return null;
                return headerProp.GetString();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Steam appdetails fetch failed for {steamId}: {ex.Message}");
                return null;
            }
        }

        public async Task<string?> ResolveSteamHeaderFallbackUrlAsync(int steamId)
        {
            var variants = new[]
            {
                $"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/header_2x.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/header.jpg"
            };

            foreach (string url in variants)
            {
                if (await IsReachableImageAsync(url).ConfigureAwait(false))
                {
                    return url;
                }
            }

            return null;
        }

        public async Task<string?> CacheImageAsync(string assetType, string stableKey, string sourceUrl, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                return null;
            }

            if (TryResolveLocalAsset(sourceUrl, out var localAssetPath))
            {
                return localAssetPath;
            }

            var assetGate = _assetGates.GetOrAdd($"cache:{assetType}:{stableKey}:{sourceUrl}", _ => new SemaphoreSlim(1, 1));
            await assetGate.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsedUri))
                    {
                        return null;
                    }

                string dir = assetType switch
                {
                    "Heroes" => GetHeroesDir(),
                    "Logos" => GetLogosDir(),
                    _ => GetCapsulesDir()
                };

                string extension = Path.GetExtension(parsedUri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".jpg";
                }

                string safeKey = SanitizeFileName(stableKey);
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl)))[..12].ToLowerInvariant();
                string filePath = Path.Combine(dir, $"{safeKey}_{hash}{extension}");

                if (File.Exists(filePath) && !force)
                {
                    return filePath;
                }

                using var response = await GetAsync(parsedUri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var remoteStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await WriteStreamAtomicallyAsync(remoteStream, filePath).ConfigureAwait(false);
                return filePath;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Asset cache download failed for '{sourceUrl}': {ex.Message}");
                    return null;
                }
            }
            finally
            {
                assetGate.Release();
            }
        }

        private static async Task WriteStreamAtomicallyAsync(Stream source, string destinationPath)
        {
            string tempPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var destination = File.Create(tempPath))
                {
                    await source.CopyToAsync(destination).ConfigureAwait(false);
                }

                File.Move(tempPath, destinationPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private static bool TryResolveLocalAsset(string value, out string localPath)
        {
            localPath = string.Empty;

            try
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var parsedUri) && parsedUri.IsFile)
                {
                    if (File.Exists(parsedUri.LocalPath))
                    {
                        localPath = parsedUri.LocalPath;
                        return true;
                    }

                    return false;
                }

                if (File.Exists(value))
                {
                    localPath = value;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private async Task<bool> IsReachableImageAsync(string url)
        {
            try
            {
                using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
                using var headResponse = await SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (headResponse.IsSuccessStatusCode && headResponse.Content.Headers.ContentType?.MediaType?.Contains("image", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }

                if ((int)headResponse.StatusCode == 404)
                {
                    return false;
                }

                using var getResponse = await GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!getResponse.IsSuccessStatusCode)
                {
                    return false;
                }

                string? mediaType = getResponse.Content.Headers.ContentType?.MediaType;
                return mediaType?.Contains("image", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch
            {
                return false;
            }
        }

        private static string SanitizeFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);

            foreach (char c in value)
            {
                builder.Append(Array.IndexOf(invalidChars, c) >= 0 ? '_' : c);
            }

            return builder.ToString();
        }
    }
}
