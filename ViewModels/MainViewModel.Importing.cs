using Codec.Models;
using Codec.Services.Importing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.ViewModels
{
    public partial class MainViewModel
    {
        private CancellationTokenSource? _importNotificationCts;

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
                IsImportStatusVisible = snapshot.IsActive;
                ImportStatusMessage = snapshot.Message;
                QueuedCount = snapshot.QueuedCount;
                ProcessingCount = snapshot.ProcessingCount;
                AddedCount = snapshot.AddedCount;
                SkippedCount = snapshot.SkippedCount;
                FailedCount = snapshot.FailedCount;
                IsOnboardingVisible = Games.Count == 0 && !snapshot.IsActive;
            });
        }

        private void ImportCoordinator_NotificationRaised(object? sender, ImportNotification notification)
        {
            _ = ShowImportNotificationAsync(notification.Title, notification.Message, notification.Severity, notification.AutoHide);
        }

        private async Task ShowImportNotificationAsync(
            string title,
            string message,
            ImportNotificationSeverity severity,
            bool autoHide = true,
            int autoHideDelayMs = 4500)
        {
            CancellationTokenSource? notificationCts = null;

            await RunOnUiThreadAsync(() =>
            {
                _importNotificationCts?.Cancel();
                _importNotificationCts?.Dispose();

                notificationCts = new CancellationTokenSource();
                _importNotificationCts = notificationCts;

                ImportNotificationTitle = title;
                ImportNotificationMessage = message;
                ImportNotificationBarSeverity = severity switch
                {
                    Codec.Services.Importing.ImportNotificationSeverity.Success => InfoBarSeverity.Success,
                    Codec.Services.Importing.ImportNotificationSeverity.Warning => InfoBarSeverity.Warning,
                    Codec.Services.Importing.ImportNotificationSeverity.Error => InfoBarSeverity.Error,
                    _ => InfoBarSeverity.Informational
                };
                IsImportNotificationVisible = true;
            });

            if (!autoHide || notificationCts == null)
            {
                return;
            }

            try
            {
                await Task.Delay(autoHideDelayMs, notificationCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                if (_importNotificationCts == notificationCts)
                {
                    IsImportNotificationVisible = false;
                    _importNotificationCts.Dispose();
                    _importNotificationCts = null;
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
