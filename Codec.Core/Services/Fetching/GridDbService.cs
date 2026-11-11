using Codec.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Codec.Services.Scanning;

namespace Codec.Services.Fetching
{
    /// <summary>
    /// Handles lookups against the SteamGridDB proxy for non-Steam titles.
    /// </summary>
    public class GridDbService
    {
        public sealed record GridAssetResolutionResult(int? GridDbId, string? CoverCachePath);

        /// <summary>
        /// The icon SteamGridDB offers for an entry. LookupFailed means the proxy could not be
        /// asked; no url without it means the entry has no icon.
        /// </summary>
        public sealed record GridIconLookup(string? Url, int? ImageId, bool LookupFailed);

        private const string GridDbSearchEndpoint = "https://codec-api-proxy.vercel.app/api/griddb/search";
        private const string GridDbIconsEndpoint = "https://codec-api-proxy.vercel.app/api/griddb/icons";
        private readonly HttpClient _http;
        private readonly GameAssetService _gameAssets;
        private readonly ScanResourceLimiter? _resourceLimiter;

        public GridDbService(GameAssetService gameAssets, ScanResourceLimiter? resourceLimiter = null, HttpMessageHandler? httpHandler = null)
        {
            _gameAssets = gameAssets;
            _resourceLimiter = resourceLimiter;
            _http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler);
            _http.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Attempts to enrich a game with SteamGridDB metadata and cover art.
        /// </summary>
        public async Task<bool> TryPopulateGridAssetsAsync(Game game, bool forceCoverDownload = false, CancellationToken cancellationToken = default)
        {
            if (game == null)
            {
                return false;
            }

            var resolution = await ResolveGridAssetsAsync(game.EffectiveMetadataLookupName, game.GridDbId, game.LibraryCapsuleCache, forceCoverDownload, cancellationToken);
            if (!resolution.GridDbId.HasValue || string.IsNullOrWhiteSpace(resolution.CoverCachePath))
            {
                return false;
            }

            game.GridDbId = resolution.GridDbId;
            game.LibraryCapsuleCache = resolution.CoverCachePath;
            return true;
        }

        public async Task<GridAssetResolutionResult> ResolveGridAssetsAsync(string? gameName, int? existingGridDbId, string? currentCoverCachePath, bool forceCoverDownload = false, CancellationToken cancellationToken = default)
        {
            int? gridDbId = existingGridDbId;
            if (!gridDbId.HasValue)
            {
                gridDbId = await FindGridDbIdAsync(gameName ?? string.Empty, cancellationToken);
                if (!gridDbId.HasValue)
                {
                    return new GridAssetResolutionResult(null, currentCoverCachePath);
                }
            }

            bool needsCover = forceCoverDownload || NeedsCover(currentCoverCachePath);
            if (!needsCover)
            {
                return new GridAssetResolutionResult(gridDbId, currentCoverCachePath);
            }

            var coverPath = await _gameAssets.DownloadGridDbCoverAsync(gridDbId.Value, forceCoverDownload, cancellationToken);
            return new GridAssetResolutionResult(gridDbId, coverPath ?? currentCoverCachePath);
        }

        /// <summary>
        /// Retrieves the first matching GridDB ID for the provided name.
        /// </summary>
        public async Task<int?> FindGridDbIdAsync(string gameName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                return null;
            }

            try
            {
                string url = $"{GridDbSearchEndpoint}?term={Uri.EscapeDataString(gameName)}";
                var response = _resourceLimiter is null
                    ? await _http.GetStringAsync(url, cancellationToken)
                    : await _resourceLimiter.RunNetworkAsync(ct => _http.GetStringAsync(url, ct), cancellationToken);
                using var doc = JsonDocument.Parse(response);

                if (!doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.GetArrayLength() == 0)
                {
                    return null;
                }

                var first = dataArray[0];
                if (first.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetInt32();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GridDB search failed for '{gameName}': {ex.Message}");
            }

            return null;
        }

        public async Task<GridIconLookup> FindIconAsync(int gridDbId, CancellationToken cancellationToken = default)
        {
            try
            {
                string url = $"{GridDbIconsEndpoint}?id={gridDbId}";
                using var response = _resourceLimiter is null
                    ? await _http.GetAsync(url, cancellationToken).ConfigureAwait(false)
                    : await _resourceLimiter.RunNetworkAsync(ct => _http.GetAsync(url, ct), cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new GridIconLookup(null, null, LookupFailed: false);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new GridIconLookup(null, null, LookupFailed: true);
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    return new GridIconLookup(null, null, LookupFailed: false);
                }

                return PickIcon(data) is { } picked
                    ? new GridIconLookup(picked.Url, picked.ImageId, LookupFailed: false)
                    : new GridIconLookup(null, null, LookupFailed: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GridDB icon lookup failed for {gridDbId}: {ex.Message}");
                return new GridIconLookup(null, null, LookupFailed: true);
            }
        }

        /// <summary>An entry marked official wins; otherwise the first one, as SteamGridDB ranks them.</summary>
        internal static (string Url, int? ImageId)? PickIcon(JsonElement data)
        {
            (string Url, int? ImageId)? first = null;
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("url", out var urlNode) ||
                    urlNode.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(urlNode.GetString()))
                {
                    continue;
                }

                int? imageId = entry.TryGetProperty("id", out var idNode) && idNode.TryGetInt32(out int id) ? id : null;
                var candidate = (urlNode.GetString()!, imageId);
                if (entry.TryGetProperty("style", out var style) &&
                    style.ValueKind == JsonValueKind.String &&
                    string.Equals(style.GetString(), "official", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                first ??= candidate;
            }

            return first;
        }

        private bool NeedsCover(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return true;
            }

            if (uri.StartsWith("https://placehold.co/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            {
                try
                {
                    return !File.Exists(parsed.LocalPath);
                }
                catch
                {
                    return true;
                }
            }

            return false;
        }
    }
}
