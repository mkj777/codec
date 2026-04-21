using Codec.Models;
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
        private Task<IReadOnlyCollection<Game>> GetLibrarySnapshotAsync()
            => RunOnUiThreadAsync<IReadOnlyCollection<Game>>(() => Games.ToList());

        private async Task CommitImportedGameAsync(Game game)
        {
            var snapshot = await RunOnUiThreadAsync(() =>
            {
                InsertGameAlphabetically(game);
                IsOnboardingVisible = false;
                return Games.ToList();
            });

            await _services.LibraryStorage.SaveAsync(snapshot).ConfigureAwait(false);
        }

        private void ImportCoordinator_StatusChanged(object? sender, GameImportStatusSnapshot snapshot)
        {
            _ = RunOnUiThreadAsync(() =>
            {
                IsScanning = snapshot.IsScanning;
                IsImportStatusVisible = snapshot.IsActive && !IsStartupScanToastVisible;
                AddedCount = snapshot.AddedCount;
                ImportRemainingCount = snapshot.QueuedCount + snapshot.ProcessingCount;
                IsOnboardingVisible = Games.Count == 0 && !snapshot.IsActive;
                if (!snapshot.IsActive)
                {
                    IsStartupScanToastVisible = false;
                    string? detected = _importCoordinator.DetectedSteamClientPath;
                    if (!string.IsNullOrEmpty(detected) && detected != _appSettings.SteamClientPath)
                    {
                        _appSettings.SteamClientPath = detected;
                        _ = _services.AppSettings.SaveAsync(_appSettings);
                    }
                }
            });
        }

        private void ImportCoordinator_NotificationRaised(object? sender, ImportNotification notification) { }

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
