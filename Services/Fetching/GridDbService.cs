using Codec.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec.Services.Fetching
{
    /// <summary>
    /// Handles lookups against the SteamGridDB proxy for non-Steam titles.
    /// </summary>
    public class GridDbService
    {
        public sealed record GridAssetResolutionResult(int? GridDbId, string? CoverCachePath);

        private const string GridDbSearchEndpoint = "https://codec-api-proxy.vercel.app/api/griddb/search";
        private readonly HttpClient _http = new();
        private readonly GameAssetService _gameAssets;

        public GridDbService(GameAssetService gameAssets)
        {
            _gameAssets = gameAssets;
        }

        /// <summary>
        /// Attempts to enrich a game with SteamGridDB metadata and cover art.
        /// </summary>
        public async Task<bool> TryPopulateGridAssetsAsync(Game game, bool forceCoverDownload = false)
        {
            if (game == null)
            {
                return false;
            }

            var resolution = await ResolveGridAssetsAsync(game.Name, game.GridDbId, game.LibCapsuleCache, forceCoverDownload);
            if (!resolution.GridDbId.HasValue || string.IsNullOrWhiteSpace(resolution.CoverCachePath))
            {
                return false;
            }

            game.GridDbId = resolution.GridDbId;
            game.LibCapsuleCache = resolution.CoverCachePath;
            return true;
        }

        public async Task<GridAssetResolutionResult> ResolveGridAssetsAsync(string? gameName, int? existingGridDbId, string? currentCoverCachePath, bool forceCoverDownload = false)
        {
            int? gridDbId = existingGridDbId;
            if (!gridDbId.HasValue)
            {
                gridDbId = await FindGridDbIdAsync(gameName ?? string.Empty);
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

            var coverPath = await _gameAssets.DownloadGridDbCoverAsync(gridDbId.Value, forceCoverDownload);
            return new GridAssetResolutionResult(gridDbId, coverPath ?? currentCoverCachePath);
        }

        /// <summary>
        /// Retrieves the first matching GridDB ID for the provided name.
        /// </summary>
        public async Task<int?> FindGridDbIdAsync(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                return null;
            }

            try
            {
                string url = $"{GridDbSearchEndpoint}?term={Uri.EscapeDataString(gameName)}";
                var response = await _http.GetStringAsync(url);
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
            catch (Exception ex)
            {
                Debug.WriteLine($"GridDB search failed for '{gameName}': {ex.Message}");
            }

            return null;
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
