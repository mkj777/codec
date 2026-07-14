using Codec.Models;
using Codec.Helpers;
using Codec.Services.Importing;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Codec.ViewModels
{
    public partial class MainViewModel
    {
        private bool _wasImportActive;

        private Task<IReadOnlyCollection<Game>> GetLibrarySnapshotAsync()
            => RunOnUiThreadAsync<IReadOnlyCollection<Game>>(() => Games.ToList());

        private async Task CommitImportedGameAsync(Game game)
        {
            var snapshot = await RunOnUiThreadAsync(() =>
            {
                if (RiotGameDuplicateHelper.IsDuplicateGame(game.ImportedFrom, game.FolderLocation, game.LaunchScript, Games))
                {
                    return DeduplicateRiotGamesInMemory();
                }

                InsertGameSorted(game);
                IsOnboardingVisible = false;
                return DeduplicateRiotGamesInMemory();
            });

            await _services.LibraryStorage.SaveAsync(snapshot).ConfigureAwait(false);
        }

        private List<Game> DeduplicateRiotGamesInMemory()
        {
            var deduplicated = RiotGameDuplicateHelper.DeduplicateByIdentity(Games);
            if (deduplicated.Count == Games.Count)
            {
                return deduplicated;
            }

            Games.CollectionChanged -= Games_CollectionChanged;
            Games.Clear();
            foreach (var existingGame in GetSortedGames(deduplicated))
            {
                Games.Add(existingGame);
            }
            Games.CollectionChanged += Games_CollectionChanged;
            OnPropertyChanged(nameof(HasGames));
            OnPropertyChanged(nameof(IsEmptyLibrary));
            OnPropertyChanged(nameof(IsLibraryVisible));
            RefreshSidebarFilteredGames();
            RefreshAvailableImportSources();
            RefreshDisplayedGames();

            return Games.ToList();
        }

        private void ImportCoordinator_StatusChanged(object? sender, GameImportStatusSnapshot snapshot)
        {
            _ = RunOnUiThreadAsync(() =>
            {
                bool wasActive = _wasImportActive;
                _wasImportActive = snapshot.IsActive;

                IsScanning = snapshot.IsScanning;
                IsImportActive = snapshot.IsActive;
                IsImportStatusVisible = snapshot.IsActive && !_isBackgroundStartupScan;
                AddedCount = snapshot.AddedCount;
                ImportRemainingCount = snapshot.QueuedCount + snapshot.ProcessingCount;
                IsOnboardingVisible = Games.Count == 0 && !snapshot.IsActive;
                if (!snapshot.IsActive)
                {
                    bool wasBackgroundStartupScan = _isBackgroundStartupScan;
                    _isBackgroundStartupScan = false;
                    bool settingsChanged = false;
                    string? detectedSteam = _importCoordinator.DetectedSteamClientPath;
                    if (!string.IsNullOrEmpty(detectedSteam) && detectedSteam != _appSettings.SteamClientPath)
                    {
                        _appSettings.SteamClientPath = detectedSteam;
                        settingsChanged = true;
                    }

                    string? detectedEpic = _importCoordinator.DetectedEpicLauncherPath;
                    if (!string.IsNullOrEmpty(detectedEpic) && detectedEpic != _appSettings.EpicLauncherPath)
                    {
                        _appSettings.EpicLauncherPath = detectedEpic;
                        settingsChanged = true;
                    }

                    if (settingsChanged)
                    {
                        _ = _services.AppSettings.SaveAsync(_appSettings);
                    }

                    if (wasActive && (!wasBackgroundStartupScan || snapshot.AddedCount > 0))
                    {
                        ScanCompleteAddedCount = snapshot.AddedCount;
                        IsScanCompleteVisible = true;
                        _ = DismissScanCompleteAsync();
                    }
                }
            });
        }

        private async Task DismissScanCompleteAsync()
        {
            await Task.Delay(4000).ConfigureAwait(false);
            _dispatcherQueue.TryEnqueue(() => IsScanCompleteVisible = false);
        }

        private void ImportCoordinator_NotificationRaised(object? sender, ImportNotification notification)
        {
            if (!notification.IsManual || notification.Severity == ImportNotificationSeverity.Success)
                return;

            _ = RunOnUiThreadAsync(() =>
            {
                if (notification.IsAlreadyAdded)
                {
                    IsGameAlreadyAddedToastVisible = true;
                    _ = DismissGameAlreadyAddedToastAsync();
                }
                else
                {
                    IsGameNotAddedToastVisible = true;
                    _ = DismissGameNotAddedToastAsync();
                }
            });
        }

        private Task RunOnUiThreadAsync(Action action)
        {
            if (_dispatcherQueue.HasThreadAccess)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<object?>();
            _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                try
                {
                    action();
                    tcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        private Task<T> RunOnUiThreadAsync<T>(Func<T> action)
        {
            if (_dispatcherQueue.HasThreadAccess)
            {
                return Task.FromResult(action());
            }

            var tcs = new TaskCompletionSource<T>();
            _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                try
                {
                    tcs.SetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }
    }
}
