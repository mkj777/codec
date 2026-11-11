using Codec.Services.Storage;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
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
        private readonly HttpClient _http;
        private readonly SteamKitService? _steamKit;
        private readonly ScanResourceLimiter? _resourceLimiter;
        private readonly string _assetsRoot;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _assetGates = new(StringComparer.OrdinalIgnoreCase);

        public enum AssetFetchOutcome
        {
            Cached,
            NotAvailable,
            Failed
        }

        /// <summary>
        /// NotAvailable means the source answered that the image does not exist. Failed means the
        /// source could not be asked; Path then still points at the previous file when there is one.
        /// </summary>
        public sealed record AssetFetchResult(AssetFetchOutcome Outcome, string? Path);

        public GameAssetService(
            SteamKitService? steamKit = null,
            ScanResourceLimiter? resourceLimiter = null,
            HttpMessageHandler? httpHandler = null,
            string? assetsRootDirectory = null)
        {
            _steamKit = steamKit;
            _resourceLimiter = resourceLimiter;
            _http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler);
            _http.Timeout = TimeSpan.FromSeconds(30);
            _assetsRoot = assetsRootDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LibraryStorageService.AppDataFolderName,
                "Assets");
        }

        private Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead, CancellationToken cancellationToken = default) =>
            _resourceLimiter is null
                ? _http.GetAsync(url, completionOption, cancellationToken)
                : _resourceLimiter.RunNetworkAsync(ct => _http.GetAsync(url, completionOption, ct), cancellationToken);

        private Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead, CancellationToken cancellationToken = default) =>
            _resourceLimiter is null
                ? _http.GetAsync(url, completionOption, cancellationToken)
                : _resourceLimiter.RunNetworkAsync(ct => _http.GetAsync(url, completionOption, ct), cancellationToken);

        private Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default) =>
            _resourceLimiter is null
                ? _http.GetStringAsync(url, cancellationToken)
                : _resourceLimiter.RunNetworkAsync(ct => _http.GetStringAsync(url, ct), cancellationToken);

        private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken = default) =>
            _resourceLimiter is null
                ? _http.SendAsync(request, completionOption, cancellationToken)
                : _resourceLimiter.RunNetworkAsync(ct => _http.SendAsync(request, completionOption, ct), cancellationToken);

        private string GetAssetDir(string name)
        {
            string baseDir = Path.Combine(_assetsRoot, name);
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        private string GetCapsulesDir() => GetAssetDir("Capsules");

        private string GetGridDbDir() => GetAssetDir("GridDb");

        private string GetHeroesDir() => GetAssetDir("Heroes");

        private string GetLogosDir() => GetAssetDir("Logos");

        private string GetIconsDir() => GetAssetDir("Icons");

        /// <summary>
        /// Writes an icon that did not come from a download (a local copy or one taken out of an
        /// exe) into the icon cache. A file already there under that name is kept as it is.
        /// </summary>
        public async Task<string?> StoreIconAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(fileName) || content.Length == 0)
            {
                return null;
            }

            try
            {
                string filePath = Path.Combine(GetIconsDir(), SanitizeFileName(fileName));
                if (File.Exists(filePath))
                {
                    return filePath;
                }

                using var source = new MemoryStream(content, writable: false);
                await WriteStreamAtomicallyAsync(source, filePath, cancellationToken).ConfigureAwait(false);
                return filePath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Icon store failed for '{fileName}': {ex.Message}");
                return null;
            }
        }

        // A missing image is a 4xx answer; throttling, timeouts and server errors say nothing about it.
        private static bool IsDefinitelyUnavailable(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return code >= 400 && code < 500
                && statusCode != HttpStatusCode.RequestTimeout
                && statusCode != HttpStatusCode.TooManyRequests;
        }

        /// <summary>
        /// Downloads the Steam library cover for a given Steam App ID.
        /// Attempts several known variants and returns the first successful local file path.
        /// If force is true, existing local files are overwritten.
        /// </summary>
        public async Task<string?> DownloadSteamLibraryCoverAsync(int steamId, bool force = false, CancellationToken cancellationToken = default)
        {
            var assetGate = _assetGates.GetOrAdd($"steam-cover:{steamId}", _ => new SemaphoreSlim(1, 1));
            await assetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                            using var picsResponse = await GetAsync(assets.CapsuleUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                            if (picsResponse.IsSuccessStatusCode)
                            {
                                await using var remote = await picsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                                await WriteStreamAtomicallyAsync(remote, filePath, cancellationToken).ConfigureAwait(false);
                                return filePath;
                            }

                            if (!IsDefinitelyUnavailable(picsResponse.StatusCode) && File.Exists(filePath))
                            {
                                return filePath;
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception picsEx)
                        {
                            Debug.WriteLine($"PICS capsule download failed for {steamId}: {picsEx.Message}");
                            if (File.Exists(filePath))
                            {
                                return filePath;
                            }
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
                    bool hadFile = File.Exists(filePath);

                    if (hadFile && !force)
                    {
                        return filePath;
                    }

                    // The old file stays in place until a replacement has fully arrived, so a
                    // failed refresh keeps the cover the game already shows.
                    try
                    {
                        using var response = await GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            Debug.WriteLine($"Cover variant not available for {steamId}: {url} -> {(int)response.StatusCode}");
                            if (hadFile && !IsDefinitelyUnavailable(response.StatusCode))
                            {
                                return filePath;
                            }
                            continue;
                        }

                        await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        await WriteStreamAtomicallyAsync(remoteStream, filePath, cancellationToken).ConfigureAwait(false);

                        return filePath;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception variantEx)
                    {
                        Debug.WriteLine($"Cover variant download failed for {steamId}: {url} -> {variantEx.Message}");
                        if (hadFile)
                        {
                            return filePath;
                        }
                    }
                }

                string? headerImageUrl = await FetchSteamHeaderImageUrlAsync(steamId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(headerImageUrl))
                {
                    string? fallbackPath = await CacheImageAsync("Capsules", $"steam_{steamId}_header_image", headerImageUrl, force, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(fallbackPath))
                    {
                        return fallbackPath;
                    }
                }

                Debug.WriteLine($"No Steam cover found for {steamId} across known variants.");
                return null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
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
        public async Task<string?> DownloadGridDbCoverAsync(int gridDbId, bool force = false, CancellationToken cancellationToken = default)
        {
            var assetGate = _assetGates.GetOrAdd($"grid-cover:{gridDbId}", _ => new SemaphoreSlim(1, 1));
            await assetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    string gridsUrl = $"https://codec-api-proxy.vercel.app/api/griddb/grids?id={gridDbId}";
                var response = await GetStringAsync(gridsUrl, cancellationToken).ConfigureAwait(false);
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

                using var downloadResponse = await GetAsync(gridUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!downloadResponse.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"GridDB cover variant not available for {gridDbId}: {gridUrl} -> {(int)downloadResponse.StatusCode}");
                    return !IsDefinitelyUnavailable(downloadResponse.StatusCode) && File.Exists(filePath) ? filePath : null;
                }

                await using var remoteStream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await WriteStreamAtomicallyAsync(remoteStream, filePath, cancellationToken).ConfigureAwait(false);

                return filePath;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
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

        public async Task<string?> FetchSteamHeaderImageUrlAsync(int steamId, CancellationToken cancellationToken = default)
        {
            try
            {
                string json = await GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={steamId}", cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Steam appdetails fetch failed for {steamId}: {ex.Message}");
                return null;
            }
        }

        public async Task<string?> ResolveSteamHeaderFallbackUrlAsync(int steamId, CancellationToken cancellationToken = default)
        {
            var variants = new[]
            {
                $"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/header_2x.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{steamId}/header.jpg"
            };

            foreach (string url in variants)
            {
                if (await IsReachableImageAsync(url, cancellationToken).ConfigureAwait(false))
                {
                    return url;
                }
            }

            return null;
        }

        public async Task<string?> CacheImageAsync(string assetType, string stableKey, string sourceUrl, bool force = false, CancellationToken cancellationToken = default)
        {
            var result = await CacheImageWithOutcomeAsync(assetType, stableKey, sourceUrl, force, cancellationToken).ConfigureAwait(false);
            return result.Path;
        }

        public async Task<AssetFetchResult> CacheImageWithOutcomeAsync(string assetType, string stableKey, string sourceUrl, bool force = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                return new AssetFetchResult(AssetFetchOutcome.NotAvailable, null);
            }

            if (TryResolveLocalAsset(sourceUrl, out var localAssetPath))
            {
                return new AssetFetchResult(AssetFetchOutcome.Cached, localAssetPath);
            }

            var assetGate = _assetGates.GetOrAdd($"cache:{assetType}:{stableKey}:{sourceUrl}", _ => new SemaphoreSlim(1, 1));
            await assetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? filePath = null;
            try
            {
                try
                {
                    if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsedUri))
                    {
                        return new AssetFetchResult(AssetFetchOutcome.NotAvailable, null);
                    }

                string dir = assetType switch
                {
                    "Heroes" => GetHeroesDir(),
                    "Logos" => GetLogosDir(),
                    "Icons" => GetIconsDir(),
                    _ => GetCapsulesDir()
                };

                string extension = Path.GetExtension(parsedUri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".jpg";
                }

                string safeKey = SanitizeFileName(stableKey);
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl)))[..12].ToLowerInvariant();
                filePath = Path.Combine(dir, $"{safeKey}_{hash}{extension}");

                if (File.Exists(filePath) && !force)
                {
                    return new AssetFetchResult(AssetFetchOutcome.Cached, filePath);
                }

                using var response = await GetAsync(parsedUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return IsDefinitelyUnavailable(response.StatusCode)
                        ? new AssetFetchResult(AssetFetchOutcome.NotAvailable, null)
                        : new AssetFetchResult(AssetFetchOutcome.Failed, File.Exists(filePath) ? filePath : null);
                }

                await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await WriteStreamAtomicallyAsync(remoteStream, filePath, cancellationToken).ConfigureAwait(false);
                return new AssetFetchResult(AssetFetchOutcome.Cached, filePath);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Asset cache download failed for '{sourceUrl}': {ex.Message}");
                    return new AssetFetchResult(AssetFetchOutcome.Failed, filePath != null && File.Exists(filePath) ? filePath : null);
                }
            }
            finally
            {
                assetGate.Release();
            }
        }

        private static async Task WriteStreamAtomicallyAsync(Stream source, string destinationPath, CancellationToken cancellationToken = default)
        {
            string tempPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var destination = File.Create(tempPath))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
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

        private async Task<bool> IsReachableImageAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
                using var headResponse = await SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (headResponse.IsSuccessStatusCode && headResponse.Content.Headers.ContentType?.MediaType?.Contains("image", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }

                if ((int)headResponse.StatusCode == 404)
                {
                    return false;
                }

                using var getResponse = await GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!getResponse.IsSuccessStatusCode)
                {
                    return false;
                }

                string? mediaType = getResponse.Content.Headers.ContentType?.MediaType;
                return mediaType?.Contains("image", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
