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
            // Hidden games wait for their metadata until they are restored.
            var candidates = await RunOnUiThreadAsync(() => Games
                .Where(game => !game.IsHidden)
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

                // Each game stands alone: one that throws keeps what it already found, and the
                // rest of the batch is still applied below.
                try
                {
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
                        bool steamLookupFailed = false;
                        if (!snapshot.IgdbId.HasValue && snapshot.EffectiveSteamMetadataAppId.HasValue)
                        {
                            var lookup = await _services.Igdb
                                .LookupIgdbIdBySteamIdAsync(snapshot.EffectiveSteamMetadataAppId.Value)
                                .ConfigureAwait(false);
                            steamLookupFailed = lookup.LookupFailed;
                            snapshot.IgdbId = lookup.IgdbId;
                            igdbIdUpdated = snapshot.IgdbId.HasValue;
                        }

                        // A name match is only a stand-in for a Steam mapping IGDB does not have.
                        // When IGDB could not be asked, guessing by name would store a weaker id
                        // that the next pass never revisits.
                        if (!snapshot.IgdbId.HasValue && !steamLookupFailed)
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
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Metadata] Completing {snapshot.Name} failed: {ex.Message}");
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

        /// <summary>
        /// Re-downloads every cover. It walks the whole library, so it runs alone: without the
        /// guard Settings could start a second pass on top of the first.
        /// </summary>
        [CommunityToolkit.Mvvm.Input.RelayCommand]
        public Task RefreshCoversAsync() => RunExclusiveAsync(RefreshCoversCoreAsync);

        // Runs on the UI thread. It walks a copy of the list because scans, Steam sync and
        // removals keep changing Games across the awaits; every write happens back on the UI
        // thread, so a game removed mid-walk is only updated as a detached object.
        private async Task RefreshCoversCoreAsync()
        {
            List<Game> games = Games.Where(game => !game.IsHidden).ToList();
            ShowScanProgress("Loading covers", games.Count == 0);
            PrepareCoverProgress(games.Count, "Update Cover", "No Games to update.");

            try
            {
                int processed = 0;
                foreach (var g in games)
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
                        var resolution = await _services.GridDb.ResolveGridAssetsAsync(
                            g.EffectiveMetadataLookupName,
                            g.GridDbId,
                            g.LibraryCapsuleCache,
                            forceCoverDownload: true);
                        if (resolution.GridDbId.HasValue && !string.IsNullOrWhiteSpace(resolution.CoverCachePath))
                        {
                            g.GridDbId = resolution.GridDbId;
                            g.LibraryCapsuleCache = resolution.CoverCachePath;
                        }
                        NotifySamePathCoverRefresh(g, previousCoverPath);
                        await Task.Delay(75);
                    }

                    processed++;
                    UpdateCoverProgress(processed, games.Count, "Updating Cover");
                }

                await _services.LibraryStorage.SaveAsync(Games.ToList());
            }
            finally
            {
                HideScanProgress();
            }
        }

        private async Task SilentUpdateImagesAsync(IEnumerable<Game> gamesToUpdate)
        {
            await _importCoordinator.WaitForIdleAsync().ConfigureAwait(false);

            // The workers only read copies taken on the UI thread, and compare with or write to
            // the bound game back on it. These copies only pick what is stale; the one a worker
            // downloads from is taken after the pause, which can outlast a whole Steam sync.
            var candidates = await RunOnUiThreadAsync(() => gamesToUpdate
                .Select(game => (Target: game, Snapshot: game.CreateHydrationSnapshot()))
                .ToList()).ConfigureAwait(false);

            DateTime staleBefore = DateTime.UtcNow.AddDays(-7);
            var games = candidates
                .Where(candidate => NeedsImageRefresh(candidate.Snapshot, staleBefore))
                .Select(candidate => candidate.Target)
                .ToList();
            int anyChanged = 0;
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = _services.ScanConcurrency.BackgroundWorkers
            };

            await Parallel.ForEachAsync(games, options, async (game, _) =>
            {
                await WaitForSilentImageUpdateResumeAsync().ConfigureAwait(false);

                string gameName = string.Empty;
                try
                {
                    Game snapshot = await RunOnUiThreadAsync(() => game.CreateHydrationSnapshot()).ConfigureAwait(false);
                    gameName = snapshot.Name;
                    var displayedAssets = await _services.DisplayedAssets
                        .EnsureDisplayedAssetsAsync(snapshot, force: true).ConfigureAwait(false);

                    await RunOnUiThreadAsync(() =>
                    {
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
                    Debug.WriteLine($"[SilentUpdate] Failed to update assets for {gameName}: {ex.Message}");
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

        // ---------------------------------------------------------------------------------
        // Icons
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Gives visible games without an icon, or with a Steam icon Steam has since replaced,
        /// one. The search runs on copies; the bound games only change back on the UI thread.
        /// </summary>
        private async Task BackfillGameIconsAsync(CancellationToken cancellationToken)
        {
            await _importCoordinator.WaitForIdleAsync().ConfigureAwait(false);

            var candidates = await RunOnUiThreadAsync(() => Games
                .Where(game => !game.IsHidden)
                .Select(game => game.CreateHydrationSnapshot())
                .ToList()).ConfigureAwait(false);

            var updates = await _services.GameIcons.BackfillAsync(
                candidates,
                _services.ScanConcurrency.BackgroundWorkers,
                WaitForSilentImageUpdateResumeAsync,
                cancellationToken).ConfigureAwait(false);
            if (updates.Count == 0)
            {
                return;
            }

            List<Game> library = await RunOnUiThreadAsync(() =>
            {
                foreach (var update in updates)
                {
                    Game? game = Games.FirstOrDefault(item => item.Id == update.GameId);
                    if (game == null)
                        continue;

                    string? previousPath = game.LibraryIconCache;
                    game.LibraryIconCache = update.Path;
                    // A file that came back under the path the game already had changes no value.
                    if (string.Equals(previousPath, update.Path, StringComparison.OrdinalIgnoreCase))
                        game.NotifyLibraryIconChanged();
                }

                return Games.ToList();
            }).ConfigureAwait(false);

            await _services.LibraryStorage.SaveAsync(library).ConfigureAwait(false);
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
                game.NotifyLibraryCapsuleChanged();
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
            IsScanProgressVisible = true;
            IsLoadingIndeterminate = isIndeterminate;
            LoadingProgress = 0;
            SetLoadingCopy(message, string.Empty);
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
            // One activity and one percentage. Never a raw count.
            ScanProgressIsIndeterminate = false;
            ScanProgressMinimum = 0;
            ScanProgressMaximum = totalGames;
            ScanProgressValue = 0;
            ScanProgressMessage = WithPercent(labelPrefix ?? "Loading covers", 0);
            LoadingSubtitle = string.Empty;
            IsLoadingIndeterminate = false;
            LoadingProgress = 0;
        }

        private void UpdateCoverProgress(int processed, int total, string? labelPrefix = null)
        {
            if (total <= 0) return;
            int done = Math.Min(processed, total);
            double percent = (double)done / total * 100d;

            ScanProgressIsIndeterminate = false;
            ScanProgressValue = done;
            ScanProgressMessage = WithPercent(labelPrefix ?? "Loading covers", percent);
            LoadingTitle = labelPrefix ?? "Loading covers";
            LoadingSubtitle = string.Empty;
            IsLoadingIndeterminate = false;
            LoadingProgress = percent;
        }

        private void HideScanProgress()
        {
            IsScanProgressVisible = false;
        }

    }
}
