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

            if (!game.GridDbId.HasValue)
            {
                int? gridId = await FindGridDbIdAsync(game.Name);
                if (!gridId.HasValue)
                {
                    return false;
                }
                game.GridDbId = gridId;
            }

            bool needsCover = forceCoverDownload || NeedsCover(game.LibCapsule);
            if (!needsCover)
            {
                return false;
            }

            var coverPath = await _gameAssets.DownloadGridDbCoverAsync(game.GridDbId.Value, forceCoverDownload);
            if (string.IsNullOrEmpty(coverPath))
            {
                return false;
            }

            game.LibCapsuleUrl = coverPath;
            return true;
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
