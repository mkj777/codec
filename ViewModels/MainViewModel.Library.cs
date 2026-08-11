using Codec.Models;
using Codec.Services.Fetching;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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

        private async Task CompleteMissingGameMetadataAsync(CancellationToken cancellationToken)
        {
            var candidates = await RunOnUiThreadAsync(() => Games
                .Where(game => !game.IsIgdbTaxonomyChecked ||
                               (game.SteamID.HasValue && game.ControllerSupport == ControllerSupportLevel.Unknown))
                .Select(game => (Target: game, Snapshot: game.CreateHydrationSnapshot()))
                .ToList()).ConfigureAwait(false);

            if (candidates.Count == 0)
            {
                return;
            }

            var updates = new ConcurrentQueue<(Game Target, Game Snapshot, bool Taxonomy, bool Controller, bool MetadataIdentity, bool IgdbId)>();
            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _services.ScanConcurrency.BackgroundWorkers
            };

            await Parallel.ForEachAsync(candidates, options, async (candidate, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Game snapshot = candidate.Snapshot;
                bool taxonomyUpdated = false;
                bool controllerUpdated = false;
                bool metadataIdentityUpdated = false;
                bool igdbIdUpdated = false;

                if (snapshot.SteamID.HasValue && snapshot.UsesAlternateMetadataLookupName && !snapshot.SteamMetadataAppId.HasValue)
                {
                    var resolved = await _services.GameName
                        .FindGameIdsByNameAsync(snapshot.EffectiveMetadataLookupName, ct)
                        .ConfigureAwait(false);
                    if (resolved.steamId.HasValue && resolved.steamId.Value != snapshot.SteamID.Value)
                    {
                        snapshot.SteamMetadataAppId = resolved.steamId.Value;
                        if (!string.IsNullOrWhiteSpace(resolved.steamName))
                        {
                            snapshot.MetadataLookupName = resolved.steamName;
                        }
                        metadataIdentityUpdated = true;
                    }
                }

                if (!snapshot.IsIgdbTaxonomyChecked)
                {
                    if (!snapshot.IgdbId.HasValue && snapshot.EffectiveSteamMetadataAppId.HasValue)
                    {
                        snapshot.IgdbId = await _services.Igdb
                            .FindIgdbIdBySteamIdAsync(snapshot.EffectiveSteamMetadataAppId.Value)
                            .ConfigureAwait(false);
                        igdbIdUpdated = snapshot.IgdbId.HasValue;
                    }

                    if (!snapshot.IgdbId.HasValue)
                    {
                        var match = await _services.Igdb
                            .FindIgdbMatchByNameAsync(snapshot.EffectiveMetadataLookupName)
                            .ConfigureAwait(false);
                        snapshot.IgdbId = match.Id;
                        igdbIdUpdated = snapshot.IgdbId.HasValue;
                    }

                    if (snapshot.IgdbId.HasValue)
                    {
                        taxonomyUpdated = await _services.Igdb
                            .PopulateTaxonomyFromIgdbAsync(snapshot)
                            .ConfigureAwait(false);
                    }
                }

                if (snapshot.EffectiveSteamMetadataAppId.HasValue &&
                    snapshot.ControllerSupport == ControllerSupportLevel.Unknown)
                {
                    controllerUpdated = await _services.SteamDetails
                        .PopulateControllerMetadataAsync(snapshot)
                        .ConfigureAwait(false);
                }

                if (taxonomyUpdated || controllerUpdated || metadataIdentityUpdated || igdbIdUpdated)
                {
                    updates.Enqueue((candidate.Target, snapshot, taxonomyUpdated, controllerUpdated, metadataIdentityUpdated, igdbIdUpdated));
                }
            }).ConfigureAwait(false);

            if (updates.IsEmpty)
            {
                return;
            }

            List<Game> library = await RunOnUiThreadAsync(() =>
            {
                while (updates.TryDequeue(out var update))
                {
                    if (update.IgdbId)
                    {
                        update.Target.IgdbId = update.Snapshot.IgdbId;
                    }

                    if (update.MetadataIdentity)
                    {
                        update.Target.SteamMetadataAppId = update.Snapshot.SteamMetadataAppId;
                        update.Target.MetadataLookupName = update.Snapshot.MetadataLookupName;
                    }

                    if (update.Taxonomy)
                    {
                        update.Target.Genres = new List<string>(update.Snapshot.Genres ?? []);
                        update.Target.Themes = new List<string>(update.Snapshot.Themes ?? []);
                        update.Target.GameModes = new List<string>(update.Snapshot.GameModes ?? []);
                    }

                    if (update.Controller)
                    {
                        update.Target.ControllerSupport = update.Snapshot.ControllerSupport;
                        update.Target.IsControllerRecommended = update.Snapshot.IsControllerRecommended;
                    }
                }

                return Games.ToList();
            }).ConfigureAwait(false);

            await _services.LibraryStorage.SaveAsync(library).ConfigureAwait(false);
        }

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
