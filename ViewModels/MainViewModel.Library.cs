using Codec.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Codec.ViewModels
{
    public partial class MainViewModel
    {
        // ---------------------------------------------------------------------------------
        // Cover Management
        // ---------------------------------------------------------------------------------

        private async Task EnsureCoversAsync(IEnumerable<Game> games)
        {
            foreach (var g in games)
            {
                bool needsCover = IsPlaceholder(g.LibCapsule) || LocalFileMissing(g.LibCapsule);

                if (g.SteamID.HasValue && needsCover)
                {
                    try
                    {
                        Debug.WriteLine($"Fetching cover for {g.Name} (SteamID {g.SteamID})");
                        var cover = await _services.GameAssets.DownloadSteamLibraryCoverAsync(g.SteamID.Value);
                        if (!string.IsNullOrEmpty(cover))
                            g.LibCapsuleCache = cover;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Cover fetch failed for {g.Name} ({g.SteamID}): {ex.Message}");
                    }
                    await Task.Delay(75);
                }
                else if (!g.SteamID.HasValue && needsCover)
                {
                    await _services.GridDb.TryPopulateGridAssetsAsync(g);
                    await Task.Delay(75);
                }
            }
        }

        private Task EnsureCoverForGameAsync(Game game) => EnsureCoversAsync(new[] { game });

        public async Task RefreshCoversAsync()
        {
            ShowScanProgress("Fetching Covers...", Games.Count == 0);
            PrepareCoverProgress(Games.Count, "Update Cover", "No Games to update.");

            int processed = 0;
            foreach (var g in Games)
            {
                if (g.SteamID.HasValue)
                {
                    var cover = await _services.GameAssets.DownloadSteamLibraryCoverAsync(g.SteamID.Value, force: true);
                    if (!string.IsNullOrEmpty(cover))
                        g.LibCapsuleCache = cover;
                    await Task.Delay(75);
                }
                else
                {
                    await _services.GridDb.TryPopulateGridAssetsAsync(g, forceCoverDownload: true);
                    await Task.Delay(75);
                }

                processed++;
                UpdateCoverProgress(processed, Games.Count, "Updating Cover");
            }

            await _services.LibraryStorage.SaveAsync(Games.ToList());
            HideScanProgress();
        }

        private async Task PopulateGridDbDataAsync(IEnumerable<Game> games)
        {
            foreach (var game in games)
            {
                if (game.SteamID.HasValue)
                    continue;
                await _services.GridDb.TryPopulateGridAssetsAsync(game);
                await Task.Delay(75);
            }
        }

        // ---------------------------------------------------------------------------------
        // Background Prefetch
        // ---------------------------------------------------------------------------------

        private void QueueBackgroundPrefetch(IEnumerable<Game> games)
        {
            foreach (var game in games)
            {
                QueueSteamWarmups(game);
                QueueRawgWarmups(game);
                QueueHltbWarmups(game);
            }
        }

        private void QueueSteamWarmups(Game game)
        {
            if (!game.SteamID.HasValue) return;
            int id = game.SteamID.Value;
            _services.Cache.QueueWarmup("steam", $"https://store.steampowered.com/api/appdetails?appids={id}", TimeSpan.FromDays(1));
            _services.Cache.QueueWarmup("steam", $"https://store.steampowered.com/appreviews/{id}/?json=1&language=all&filter=all&num_per_page=0", TimeSpan.FromHours(6));
            _services.Cache.QueueWarmup("steam", $"https://steamspy.com/api.php?request=appdetails&appid={id}", TimeSpan.FromHours(4));
        }

        private void QueueRawgWarmups(Game game)
        {
            if (game.RawgID.HasValue)
            {
                _services.Cache.QueueWarmup("rawg", $"https://codec-api-proxy.vercel.app/api/rawg/details?id={game.RawgID.Value}", TimeSpan.FromDays(1));
                return;
            }
            if (!string.IsNullOrWhiteSpace(game.Name))
            {
                string term = Uri.EscapeDataString(game.Name);
                _services.Cache.QueueWarmup("rawg", $"https://codec-api-proxy.vercel.app/api/rawg/search?term={term}", TimeSpan.FromDays(1));
            }
        }

        private void QueueHltbWarmups(Game game)
        {
            if (string.IsNullOrWhiteSpace(game.Name)) return;
            string normalized = NormalizeForHltb(game.Name);
            if (!string.IsNullOrWhiteSpace(normalized))
                _services.Cache.QueueWarmup("hltb", $"https://codec-api-proxy.vercel.app/api/hltb/search?term={Uri.EscapeDataString(normalized)}", TimeSpan.FromDays(7));
        }

        private static string NormalizeForHltb(string value)
        {
            string cleaned = Regex.Replace(value, "[^a-zA-Z0-9 ]", " ");
            cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
            return cleaned;
        }

        // ---------------------------------------------------------------------------------
        // Scan Progress Helpers
        // ---------------------------------------------------------------------------------

        private void ShowScanProgress(string message, bool isIndeterminate)
        {
            ScanProgressMessage = message;
            ScanProgressIsIndeterminate = isIndeterminate;
            ScanProgressValue = 0;
            ScanProgressMaximum = 1;
            IsScanProgressVisible = false;
            SetLoadingState(true, message, isIndeterminate ? "This will take a few minutes..." : string.Empty);
        }

        private void PrepareCoverProgress(int totalGames, string? labelPrefix = null, string? emptyMessage = null)
        {
            LoadingTitle = labelPrefix ?? "Loading covers";
            if (totalGames <= 0)
            {
                ScanProgressIsIndeterminate = true;
                ScanProgressMessage = emptyMessage ?? "No new games found.";
                LoadingSubtitle = emptyMessage ?? "No new games found.";
                return;
            }
            ScanProgressIsIndeterminate = false;
            ScanProgressMinimum = 0;
            ScanProgressMaximum = totalGames;
            ScanProgressValue = 0;
            ScanProgressMessage = $"{labelPrefix ?? "Loading covers"} (0/{totalGames})";
            LoadingSubtitle = $"Preparing artwork... (0/{totalGames})";
        }

        private void UpdateCoverProgress(int processed, int total, string? labelPrefix = null)
        {
            if (total <= 0) return;
            ScanProgressIsIndeterminate = false;
            ScanProgressValue = Math.Min(processed, total);
            ScanProgressMessage = $"{labelPrefix ?? "Loading covers"} ({Math.Min(processed, total)}/{total})";
            LoadingTitle = labelPrefix ?? "Loading covers";
            LoadingSubtitle = $"Preparing artwork... ({Math.Min(processed, total)}/{total})";
        }

        private void HideScanProgress()
        {
            IsScanProgressVisible = false;
            SetLoadingState(false);
        }

        // ---------------------------------------------------------------------------------
        // Utility
        // ---------------------------------------------------------------------------------

        private static bool IsPlaceholder(string? uri) =>
            string.IsNullOrWhiteSpace(uri) || uri.StartsWith("https://placehold.co/", StringComparison.OrdinalIgnoreCase);

        private static bool LocalFileMissing(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return true;
            if (File.Exists(uri)) return false;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return true;
            if (parsed.IsFile)
            {
                try { return !File.Exists(parsed.LocalPath); } catch { return true; }
            }
            return false;
        }
    }
}
