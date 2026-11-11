using CommunityToolkit.Mvvm.ComponentModel;
using Codec.Models;
using Codec.Helpers;
using Codec.Services.Importing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Codec.Services.Storage;

namespace Codec.ViewModels
{
    public partial class MainViewModel
    {
        internal const string ScanCompleteText = "Scan complete";
        internal const string ScanIncompleteText = "Scan incomplete";
        internal const string ScanFailedText = "Scan failed";
        internal const string GameAddFailedText = "Could not add that game";

        private const int ImportNoticeDismissDelayMs = 4000;

        private bool _wasImportActive;
        private int _scanResultVersion;

        /// <summary>The line the pill shows once a scan has ended.</summary>
        [ObservableProperty] private string _scanResultText = ScanCompleteText;

        /// <summary>A manual add that failed for a reason other than the file.</summary>
        [ObservableProperty] private bool _isGameAddFailedToastVisible;

        private ScanOutcome _lastScanOutcome;

        /// <summary>
        /// How the most recent scan ended, so a view can tell a finished scan from a stopped one.
        /// A session that only imported by hand leaves it as it was.
        /// </summary>
        public ScanOutcome LastScanOutcome
        {
            get => _lastScanOutcome;
            private set
            {
                // The onboarding title reads this, and the result can land after the scan
                // step has already marked itself complete.
                if (SetProperty(ref _lastScanOutcome, value))
                    OnPropertyChanged(nameof(ScanScreenTitle));
            }
        }

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

                // Onboarding is running its own scan step. Hiding it the moment the first
                // game lands drops the player into the library mid-scan, and the step that
                // reports the scan finished then pulls them back out of it.
                if (!IsOnboardingOwningScreen)
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
            OnPropertyChanged(nameof(IsEmptyLibraryVisible));
            RefreshSidebarFilteredGames();
            RefreshAvailableImportSources();
            RefreshDisplayedGames();

            return Games.ToList();
        }

        /// <summary>
        /// One activity and one percentage, never a raw count or an internal stage.
        /// Without this the notice pill had nothing to say for the whole of a scan.
        /// </summary>
        private static string DescribeImport(GameImportStatusSnapshot snapshot)
        {
            if (!snapshot.IsActive)
                return string.Empty;

            if (snapshot.IsScanning)
                return "Looking for games";

            int remaining = snapshot.QueuedCount + snapshot.ProcessingCount;
            int total = snapshot.AddedCount + remaining;
            if (total <= 0 || remaining <= 0)
                return "Adding games";

            return WithPercent("Adding games", (double)snapshot.AddedCount / total * 100d);
        }

        private void ImportCoordinator_StatusChanged(object? sender, GameImportStatusSnapshot snapshot)
        {
            ObserveFault(RunOnUiThreadAsync(() =>
            {
                bool wasActive = _wasImportActive;
                _wasImportActive = snapshot.IsActive;

                // A new session retires the last one's result, including a "Scan failed" that
                // waits to be dismissed.
                if (snapshot.IsActive && !wasActive)
                    IsScanCompleteVisible = false;

                IsScanning = snapshot.IsScanning;
                IsImportActive = snapshot.IsActive;
                IsImportStatusVisible = snapshot.IsActive && !_isBackgroundStartupScan;
                AddedCount = snapshot.AddedCount;
                ImportRemainingCount = snapshot.QueuedCount + snapshot.ProcessingCount;
                if (!IsOnboardingOwningScreen)
                {
                    IsOnboardingVisible = Games.Count == 0
                        && !snapshot.IsActive
                        && CanOfferOnboarding;
                }

                ImportStatusMessage = DescribeImport(snapshot);
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
                        ObserveFault(_services.AppSettings.SaveAsync(_appSettings), "Saving detected launcher paths");
                    }

                    if (wasActive)
                    {
                        ShowScanResult(snapshot, wasBackgroundStartupScan);
                    }
                }
            }), "Applying import status");
        }

        /// <summary>
        /// One line for how the scan ended. A clean or incomplete startup scan stays quiet unless
        /// it added something, as before; a stopped one always speaks, because silence there
        /// reads as "nothing to find" and the shelf may now be out of date.
        /// </summary>
        private void ShowScanResult(GameImportStatusSnapshot snapshot, bool wasBackgroundStartupScan)
        {
            // Recorded before either quiet exit below: a cancelled or silent startup scan still ended.
            if (snapshot.Outcome != ScanOutcome.None)
                LastScanOutcome = snapshot.Outcome;

            string? text = snapshot.Outcome switch
            {
                ScanOutcome.Cancelled => null,
                ScanOutcome.Stopped => ScanFailedText,
                ScanOutcome.Incomplete => ScanIncompleteText,
                // Completed, and None for a session that only imported manually: today's line.
                _ => ScanCompleteText,
            };

            if (text == null)
                return;

            bool isStopped = snapshot.Outcome == ScanOutcome.Stopped;
            if (!isStopped && wasBackgroundStartupScan && snapshot.AddedCount <= 0)
                return;

            ScanCompleteAddedCount = snapshot.AddedCount;
            ScanResultText = text;
            IsScanCompleteVisible = true;

            int version = ++_scanResultVersion;
            // A stopped scan waits to be dismissed, as the coordinator's failure always asked
            // (AutoHide: false). Everything else clears on its own.
            if (!isStopped)
                _ = DismissScanResultAsync(version);
        }

        private async Task DismissScanResultAsync(int version)
        {
            await Task.Delay(ImportNoticeDismissDelayMs).ConfigureAwait(false);
            _uiDispatcher.Post(() =>
            {
                // A newer result owns the line now; this timer belonged to the old one.
                if (version == _scanResultVersion)
                    IsScanCompleteVisible = false;
            });
        }

        private void ImportCoordinator_NotificationRaised(object? sender, ImportNotification notification)
        {
            if (!notification.IsManual || notification.Severity == ImportNotificationSeverity.Success)
                return;

            ObserveFault(RunOnUiThreadAsync(() =>
            {
                switch (notification.ResultStatus)
                {
                    case GameImportResultStatus.Duplicate:
                        IsGameAlreadyAddedToastVisible = true;
                        _ = DismissGameAlreadyAddedToastAsync();
                        break;
                    case GameImportResultStatus.Invalid:
                        IsGameNotAddedToastVisible = true;
                        _ = DismissGameNotAddedToastAsync();
                        break;
                    default:
                        // Network, artwork or commit trouble: the file was fine, so do not
                        // say it is not a game.
                        IsGameAddFailedToastVisible = true;
                        _ = DismissGameAddFailedToastAsync();
                        break;
                }
            }), "Showing an import notice");
        }

        private async Task DismissGameAddFailedToastAsync()
        {
            await Task.Delay(ImportNoticeDismissDelayMs).ConfigureAwait(false);
            _uiDispatcher.Post(() => IsGameAddFailedToastVisible = false);
        }

        /// <summary>
        /// For a task nobody awaits. Left alone, its fault only reaches the crash log if the
        /// finalizer happens to see it before the app exits; this writes it down when it happens.
        /// </summary>
        private static void ObserveFault(Task task, string context)
        {
            task.ContinueWith(
                static (faulted, state) => LogBackgroundFault((string)state!, faulted.Exception!),
                context,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void LogBackgroundFault(string context, Exception error)
        {
            Debug.WriteLine($"[Background] {context} failed: {error}");
            try
            {
                // Same file and shape as the crash log Program keeps for unobserved tasks.
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    LibraryStorageService.AppDataFolderName);
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "crash.log"),
                    $"{DateTime.Now:u} [background: {context}] {error}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception writeFailure)
            {
                Debug.WriteLine($"Could not write the crash log: {writeFailure.Message}");
            }
        }

        private Task RunOnUiThreadAsync(Action action)
        {
            return _uiDispatcher.InvokeAsync(action);
        }

        private Task<T> RunOnUiThreadAsync<T>(Func<T> action)
        {
            return _uiDispatcher.InvokeAsync(action);
        }
    }
}
