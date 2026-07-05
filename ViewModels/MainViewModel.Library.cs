using Codec.Helpers;
using Codec.Models;
using Codec.Services.Fetching;
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
                bool needsCover = IsPlaceholder(g.LibraryCapsule) || LocalFileMissing(g.LibraryCapsule);

                if (g.SteamID.HasValue && needsCover)
                {
                    try
                    {
                        Debug.WriteLine($"Fetching cover for {g.Name} (SteamID {g.SteamID})");
                        var cover = await _services.GameAssets.DownloadSteamLibraryCoverAsync(g.SteamID.Value);
                        if (!string.IsNullOrEmpty(cover))
                            g.LibraryCapsuleCache = cover;
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
                        g.LibraryCapsuleCache = cover;
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

        private async Task SilentUpdateImagesAsync(IEnumerable<Game> gamesToUpdate)
        {
            // Wait a short time to allow UI startup animations to complete smoothly
            await Task.Delay(3000).ConfigureAwait(false);

            var games = gamesToUpdate.ToList();
            bool anyChanged = false;

            foreach (var game in games)
            {
                try
                {
                    // Create hydration snapshot of the game
                    var snapshot = game.CreateHydrationSnapshot();

                    // Force redownload of assets on background thread
                    var displayedAssets = await _services.DisplayedAssets.EnsureDisplayedAssetsAsync(snapshot, force: true).ConfigureAwait(false);

                    // Check if anything actually changed
                    bool changed = snapshot.GridDbId != game.GridDbId ||
                                   snapshot.LibraryCapsuleCache != game.LibraryCapsuleCache ||
                                   snapshot.HasHeroAssetSource != game.HasHeroAssetSource ||
                                   snapshot.LibraryHeroUrl != game.LibraryHeroUrl ||
                                   snapshot.LibraryHeroCache != game.LibraryHeroCache ||
                                   snapshot.HasLogoAssetSource != game.HasLogoAssetSource ||
                                   snapshot.LibraryLogoUrl != game.LibraryLogoUrl ||
                                   snapshot.LibraryLogoCache != game.LibraryLogoCache ||
                                   displayedAssets.GridDbId != game.GridDbId ||
                                   displayedAssets.CapsuleCachePath != game.LibraryCapsuleCache ||
                                   displayedAssets.HasHeroSource != game.HasHeroAssetSource ||
                                   displayedAssets.HeroUrl != game.LibraryHeroUrl ||
                                   displayedAssets.HeroCachePath != game.LibraryHeroCache ||
                                   displayedAssets.HasLogoSource != game.HasLogoAssetSource ||
                                   displayedAssets.LogoUrl != game.LibraryLogoUrl ||
                                   displayedAssets.LogoCachePath != game.LibraryLogoCache;

                    if (changed)
                    {
                        anyChanged = true;

                        await RunOnUiThreadAsync(() =>
                        {
                            ApplyDisplayedAssetHydration(game, displayedAssets);
                            game.IsFullyImported = displayedAssets.AreRequiredAssetsReady;
                        }).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SilentUpdate] Failed to update assets for {game.Name}: {ex.Message}");
                }

                // Small delay to respect rate limits
                await Task.Delay(250).ConfigureAwait(false);
            }

            if (anyChanged)
            {
                try
                {
                    var currentGames = await RunOnUiThreadAsync(() => Games.ToList()).ConfigureAwait(false);
                    await _services.LibraryStorage.SaveAsync(currentGames).ConfigureAwait(false);
                    Debug.WriteLine("[SilentUpdate] Library saved with updated game images.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SilentUpdate] Failed to save updated library: {ex.Message}");
                }
            }
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
            }
        }

        private void QueueSteamWarmups(Game game)
        {
            if (!game.EffectiveSteamMetadataAppId.HasValue) return;
            int id = game.EffectiveSteamMetadataAppId.Value;
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
            if (!string.IsNullOrWhiteSpace(game.EffectiveMetadataLookupName))
            {
                string term = Uri.EscapeDataString(game.EffectiveMetadataLookupName);
                _services.Cache.QueueWarmup("rawg", $"https://codec-api-proxy.vercel.app/api/rawg/search?term={term}", TimeSpan.FromDays(1));
            }
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
            string.IsNullOrWhiteSpace(uri) ||
            uri.StartsWith("https://placehold.co/", StringComparison.OrdinalIgnoreCase) ||
            AssetUriResolver.IsBundledAssetReference(uri, "Assets/noCover.png");

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
