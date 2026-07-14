using Codec.Models;
using Codec.Services.Fetching;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.ViewModels
{
    public partial class MainViewModel
    {
        private readonly object _silentImageUpdatePauseLock = new();
        private int _silentImageUpdatePauseCount;
        private TaskCompletionSource<bool>? _silentImageUpdateResumeSignal;

        // ---------------------------------------------------------------------------------
        // Cover Management
        // ---------------------------------------------------------------------------------

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
                        SetLibraryCoverPath(g, cover);
                    await Task.Delay(75);
                }
                else
                {
                    string? previousCoverPath = g.LibraryCapsuleCache;
                    await _services.GridDb.TryPopulateGridAssetsAsync(g, forceCoverDownload: true);
                    NotifySamePathCoverRefresh(g, previousCoverPath);
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
            await _importCoordinator.WaitForIdleAsync().ConfigureAwait(false);

            DateTime staleBefore = DateTime.UtcNow.AddDays(-7);
            var games = gamesToUpdate.Where(game => NeedsImageRefresh(game, staleBefore)).ToList();
            int anyChanged = 0;
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = _services.ScanConcurrency.BackgroundWorkers
            };

            await Parallel.ForEachAsync(games, options, async (game, _) =>
            {
                await WaitForSilentImageUpdateResumeAsync().ConfigureAwait(false);

                try
                {
                    var snapshot = game.CreateHydrationSnapshot();
                    var displayedAssets = await _services.DisplayedAssets
                        .EnsureDisplayedAssetsAsync(snapshot, force: true).ConfigureAwait(false);
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
                        Interlocked.Exchange(ref anyChanged, 1);

                    await RunOnUiThreadAsync(() =>
                    {
                        string? previousCoverPath = game.LibraryCapsuleCache;
                        if (changed)
                        {
                            ApplyDisplayedAssetHydration(game, displayedAssets);
                            if (displayedAssets.AreRequiredAssetsReady)
                                game.IsFullyImported = true;
                        }
                        NotifySamePathCoverRefresh(game, previousCoverPath);
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SilentUpdate] Failed to update assets for {game.Name}: {ex.Message}");
                }

            }).ConfigureAwait(false);

            if (anyChanged != 0)
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

        private void SetLibraryCoverPath(Game game, string path)
        {
            string? previousPath = game.LibraryCapsuleCache;
            game.LibraryCapsuleCache = path;
            NotifySamePathCoverRefresh(game, previousPath);
        }

        private void NotifySamePathCoverRefresh(Game game, string? previousPath)
        {
            if (string.Equals(previousPath, game.LibraryCapsuleCache, StringComparison.OrdinalIgnoreCase))
                game.RefreshLibraryCapsuleBinding();
        }

        private void PauseSilentImageUpdate()
        {
            lock (_silentImageUpdatePauseLock)
            {
                if (_silentImageUpdatePauseCount++ == 0)
                {
                    _silentImageUpdateResumeSignal = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    Debug.WriteLine("[SilentUpdate] Pause requested by priority work.");
                }
            }
        }

        private static bool NeedsImageRefresh(Game game, DateTime staleBefore)
        {
            if (IsMissingOrStale(game.LibraryCapsuleCache, staleBefore))
                return true;

            if ((game.HasHeroAssetSource || !string.IsNullOrWhiteSpace(game.LibraryHeroUrl)) &&
                IsMissingOrStale(game.LibraryHeroCache, staleBefore))
                return true;

            return (game.HasLogoAssetSource || !string.IsNullOrWhiteSpace(game.LibraryLogoUrl)) &&
                   IsMissingOrStale(game.LibraryLogoCache, staleBefore);
        }

        private static bool IsMissingOrStale(string? path, DateTime staleBefore)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            try
            {
                string localPath = Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile
                    ? uri.LocalPath
                    : path;
                return !File.Exists(localPath) || File.GetLastWriteTimeUtc(localPath) < staleBefore;
            }
            catch
            {
                return true;
            }
        }

        private void ResumeSilentImageUpdate()
        {
            TaskCompletionSource<bool>? resumeSignal = null;

            lock (_silentImageUpdatePauseLock)
            {
                if (_silentImageUpdatePauseCount == 0 || --_silentImageUpdatePauseCount != 0)
                    return;

                resumeSignal = _silentImageUpdateResumeSignal;
                _silentImageUpdateResumeSignal = null;
            }

            Debug.WriteLine("[SilentUpdate] Priority work finished; resuming.");
            resumeSignal?.TrySetResult(true);
        }

        private async Task WaitForSilentImageUpdateResumeAsync()
        {
            Task? resumeTask;
            lock (_silentImageUpdatePauseLock)
                resumeTask = _silentImageUpdateResumeSignal?.Task;

            if (resumeTask == null)
                return;

            Debug.WriteLine("[SilentUpdate] Paused before next game.");
            await resumeTask.ConfigureAwait(false);
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

    }
}
