using Codec.Models;
using Codec.Helpers;
using Codec.Services.Logging;
using Codec.Services.Scanning;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Codec.Services.Importing
{
    public sealed class LibraryImportCoordinator : IDisposable
    {
        private readonly IGameImportPipeline _pipeline;
        private readonly Func<Task<IReadOnlyCollection<Game>>> _librarySnapshotProvider;
        private readonly Func<Game, Task> _commitImportedGameAsync;
        private readonly GameScanner _scanner;
        private sealed record ImportWorkItem(GameImportRequest Request, CancellationToken CancellationToken);

        private readonly Channel<ImportWorkItem> _queue;
        private readonly HashSet<string> _reservedExecutables = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _stateGate = new();
        private readonly CancellationTokenSource _disposeCts = new();
        private readonly SemaphoreSlim _commitGate = new(1, 1);
        private readonly ScanConcurrencyOptions _concurrency;
        private readonly List<Task> _workerTasks = new();

        private CancellationTokenSource _scanCts = new();
        private volatile bool _drainQueue;
        private Stopwatch? _clickStopwatch;

        private bool _isScanRunning;
        private int _queuedCount;
        private int _processingCount;
        private int _addedCount;
        private int _skippedCount;
        private int _failedCount;
        private int _lastCompletedSessionTotal;
        private TaskCompletionSource<bool> _idleTcs = CreateCompletedIdleSource();

        public string? DetectedSteamClientPath => _scanner.DetectedSteamClientPath;
        public string? DetectedEpicLauncherPath => _scanner.DetectedEpicLauncherPath;

        public event EventHandler<GameImportStatusSnapshot>? StatusChanged;
        public event EventHandler<ImportNotification>? NotificationRaised;

        public LibraryImportCoordinator(
            IGameImportPipeline pipeline,
            GameScanner scanner,
            Func<Task<IReadOnlyCollection<Game>>> librarySnapshotProvider,
            Func<Game, Task> commitImportedGameAsync,
            ScanConcurrencyOptions? concurrency = null)
        {
            _pipeline = pipeline;
            _scanner = scanner;
            _librarySnapshotProvider = librarySnapshotProvider;
            _commitImportedGameAsync = commitImportedGameAsync;
            _concurrency = concurrency ?? ScanConcurrencyOptions.CreateAdaptive();
            _queue = Channel.CreateBounded<ImportWorkItem>(new BoundedChannelOptions(_concurrency.ImportQueueCapacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            for (int i = 0; i < _concurrency.ImportWorkers; i++)
            {
                _workerTasks.Add(ProcessQueueAsync(i));
            }
        }

        public async Task StartScanAsync()
        {
            _clickStopwatch = Stopwatch.StartNew();

            lock (_stateGate)
            {
                if (_isScanRunning || _queuedCount > 0 || _processingCount > 0)
                {
                    RaiseNotification(new ImportNotification(
                        "Library Import",
                        "A scan is already running in the background.",
                        ImportNotificationSeverity.Warning));
                    return;
                }

                _drainQueue = false;
                ResetSessionCountsIfIdle_NoLock();
                _idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _isScanRunning = true;
            }

            ScanLogFile.BeginSession();

            PublishStatus();
            RaiseNotification(new ImportNotification(
                "Library Import",
                "Scanning for games in the background.",
                ImportNotificationSeverity.Informational));

            _ = RunScanAsync();
            await Task.CompletedTask;
        }

        public void Cancel()
        {
            _drainQueue = true;

            var old = _scanCts;
            _scanCts = new CancellationTokenSource();
            old.Cancel();
            old.Dispose();

            PublishStatus();
        }

        public async Task CancelAndDrainAsync()
        {
            Cancel();
            await WaitForIdleAsync().ConfigureAwait(false);
        }

        public Task WaitForIdleAsync()
        {
            lock (_stateGate)
            {
                if (!_isScanRunning && _queuedCount == 0 && _processingCount == 0)
                {
                    return Task.CompletedTask;
                }

                return _idleTcs.Task;
            }
        }

        private async Task RunScanAsync()
        {
            var clickSw = _clickStopwatch;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_scanCts.Token, _disposeCts.Token);
            try
            {
                await Task.Run(async () =>
                {
                    await foreach (var candidate in _scanner.ScanIncrementallyAsync(linkedCts.Token, clickStopwatch: clickSw).ConfigureAwait(false))
                    {
                        await TryEnqueueScanCandidateAsync(candidate, linkedCts.Token).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                GameScanner.LogSession($"Background scan failed: {ex.Message}");
                RaiseNotification(new ImportNotification(
                    "Library Import",
                    "The background scan stopped because of an error.",
                    ImportNotificationSeverity.Error,
                    AutoHide: false));
            }
            finally
            {
                lock (_stateGate)
                {
                    _isScanRunning = false;
                }

                PublishStatus();
                RaiseCompletionNotificationIfIdle();
            }
        }

        public async Task<ImportEnqueueResult> EnqueueManualExecutableAsync(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return new ImportEnqueueResult(ImportEnqueueResultStatus.Invalid, "No executable was selected.");
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(executablePath);
            }
            catch
            {
                return new ImportEnqueueResult(ImportEnqueueResultStatus.Invalid, "The selected executable path is invalid.");
            }

            if (!File.Exists(normalizedPath))
            {
                return new ImportEnqueueResult(ImportEnqueueResultStatus.Invalid, "The selected executable no longer exists.");
            }

            var librarySnapshot = await _librarySnapshotProvider().ConfigureAwait(false);
            if (librarySnapshot.Any(g => string.Equals(g.Executable, normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                return new ImportEnqueueResult(ImportEnqueueResultStatus.Duplicate, "This executable is already in your library.");
            }

            lock (_stateGate)
            {
                _drainQueue = false;
                ResetSessionCountsIfIdle_NoLock();
                if (_queuedCount == 0 && _processingCount == 0)
                {
                    _idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                if (!_reservedExecutables.Add(normalizedPath))
                {
                    return new ImportEnqueueResult(ImportEnqueueResultStatus.Duplicate, "This executable is already queued for import.");
                }

                _queuedCount++;
            }

            string folder = Path.GetDirectoryName(normalizedPath) ?? string.Empty;
            string nameHint = Path.GetFileNameWithoutExtension(normalizedPath);
            var manualBatch = new ScanLogBatch(nameHint, "Added manually");
            var request = new GameImportRequest(
                normalizedPath,
                folder,
                nameHint,
                "Added manually",
                IsManual: true,
                LogBatch: manualBatch);
            try
            {
                await _queue.Writer.WriteAsync(
                    new ImportWorkItem(request, _scanCts.Token),
                    _disposeCts.Token).ConfigureAwait(false);
            }
            catch
            {
                RollBackQueuedReservation(normalizedPath);
                throw;
            }

            PublishStatus();
            return new ImportEnqueueResult(ImportEnqueueResultStatus.Accepted, "Game queued for background import.");
        }

        private async Task TryEnqueueScanCandidateAsync(ValidatedScanCandidate candidate, CancellationToken cancellationToken)
        {
            if (_drainQueue)
            {
                candidate.LogBatch?.Flush("– SKIPPED", "scan cancelled / draining queue");
                return;
            }

            var batch = candidate.LogBatch;
            batch?.Log($"ENQUEUE source={candidate.ImportSource} exe={candidate.ExecutablePath} lnk={candidate.LaunchScriptPath} epic={candidate.EpicAppId} metadata='{candidate.MetadataLookupName ?? "-"}'");
            var librarySnapshot = await _librarySnapshotProvider().ConfigureAwait(false);

            bool hasExe = !string.IsNullOrWhiteSpace(candidate.ExecutablePath);
            string reservationKey = GetReservationKey(candidate);

            if (hasExe && librarySnapshot.Any(g => string.Equals(g.Executable, candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            {
                batch?.Flush("✗ DENIED", "already in library (exe match)");
                IncrementSkipped();
                return;
            }

            if (candidate.SteamAppId.HasValue && librarySnapshot.Any(g => g.SteamID == candidate.SteamAppId.Value))
            {
                batch?.Flush("✗ DENIED", $"steam id {candidate.SteamAppId} already in library");
                IncrementSkipped();
                return;
            }

            if (!string.IsNullOrWhiteSpace(candidate.EpicAppId) &&
                librarySnapshot.Any(g => string.Equals(g.EpicAppId, candidate.EpicAppId, StringComparison.OrdinalIgnoreCase)))
            {
                batch?.Flush("✗ DENIED", $"epic id {candidate.EpicAppId} already in library");
                IncrementSkipped();
                return;
            }

            if (RiotGameDuplicateHelper.IsDuplicateGame(candidate.ImportSource, candidate.FolderLocation, candidate.LaunchScriptPath, librarySnapshot))
            {
                batch?.Flush("✗ DENIED", $"Riot target already in library: folder='{candidate.FolderLocation}' lnk='{candidate.LaunchScriptPath}'");
                IncrementSkipped();
                return;
            }

            lock (_stateGate)
            {
                ResetSessionCountsIfIdle_NoLock();
                if (!_reservedExecutables.Add(reservationKey))
                {
                    batch?.Flush("✗ DENIED", $"already reserved (key={reservationKey})");
                    _skippedCount++;
                    PublishStatus_NoLock();
                    return;
                }

                _queuedCount++;
            }

            var request = new GameImportRequest(
                candidate.ExecutablePath,
                candidate.FolderLocation,
                candidate.GameName,
                candidate.ImportSource,
                candidate.SteamAppId,
                candidate.RawgId,
                IsManual: false,
                candidate.LaunchScriptPath,
                candidate.IgdbId,
                candidate.EpicAppId,
                LogBatch: batch,
                MetadataLookupName: candidate.MetadataLookupName);
            try
            {
                await _queue.Writer.WriteAsync(
                    new ImportWorkItem(request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                RollBackQueuedReservation(reservationKey);
                throw;
            }

            PublishStatus();
        }

        private async Task ProcessQueueAsync(int workerId)
        {
            try
            {
                await foreach (var item in _queue.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
                {
                    GameImportRequest request = item.Request;
                    lock (_stateGate)
                    {
                        _queuedCount = Math.Max(0, _queuedCount - 1);
                        _processingCount++;
                    }

                    if (_drainQueue || item.CancellationToken.IsCancellationRequested)
                    {
                        lock (_stateGate)
                        {
                            _processingCount = Math.Max(0, _processingCount - 1);
                            _reservedExecutables.Remove(GetReservationKey(request));
                        }

                        PublishStatus();
                        continue;
                    }

                    try
                    {
                        PublishStatus();
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(item.CancellationToken, _disposeCts.Token);
                        var librarySnapshot = await _librarySnapshotProvider().ConfigureAwait(false);
                        var result = await _pipeline.ImportAsync(request, librarySnapshot, linkedCts.Token).ConfigureAwait(false);

                        switch (result.Status)
                        {
                            case GameImportResultStatus.Added when result.Game != null && result.Game.IsFullyImported && result.Game.DisplayedAssetsReady:
                                bool committed = await CommitOrSkipAsync(result.Game, linkedCts.Token).ConfigureAwait(false);

                                if (request.IsManual && committed)
                                {
                                    RaiseNotification(new ImportNotification(
                                        "Library Import",
                                        result.Message,
                                        ImportNotificationSeverity.Success));
                                }
                                break;
                            case GameImportResultStatus.Added when result.Game != null && result.Game.IsFullyImported && !request.IsManual:
                                // Platform scanner game: commit even without full artwork
                                await CommitOrSkipAsync(result.Game, linkedCts.Token).ConfigureAwait(false);
                                break;
                            case GameImportResultStatus.Added:
                                lock (_stateGate)
                                {
                                    _failedCount++;
                                }

                                RaiseNotification(new ImportNotification(
                                    "Library Import",
                                    request.IsManual
                                        ? "Codec finished the metadata pass but the game is still missing required artwork, so it was not added."
                                        : $"Codec skipped '{request.NameHint}' because required artwork was not ready.",
                                    ImportNotificationSeverity.Error,
                                    IsManual: request.IsManual));
                                break;
                            case GameImportResultStatus.Duplicate:
                            case GameImportResultStatus.Invalid:
                                lock (_stateGate)
                                {
                                    _skippedCount++;
                                }

                                if (request.IsManual)
                                {
                                    RaiseNotification(new ImportNotification(
                                        "Library Import",
                                        result.Message,
                                        ImportNotificationSeverity.Warning,
                                        IsManual: true,
                                        IsAlreadyAdded: result.Status == GameImportResultStatus.Duplicate));
                                }
                                break;
                            default:
                                lock (_stateGate)
                                {
                                    _failedCount++;
                                }

                                RaiseNotification(new ImportNotification(
                                    "Library Import",
                                    request.IsManual ? result.Message : $"Codec could not finish importing '{request.NameHint}'.",
                                    ImportNotificationSeverity.Error,
                                    IsManual: request.IsManual));
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (item.CancellationToken.IsCancellationRequested || _disposeCts.IsCancellationRequested)
                    {
                        request.LogBatch?.Flush("– CANCELLED", $"worker {workerId} cancelled");
                    }
                    catch (Exception ex)
                    {
                        GameScanner.LogSession($"Commit failed for '{request.ExecutablePath}': {ex.Message}");
                        lock (_stateGate)
                        {
                            _failedCount++;
                        }

                        RaiseNotification(new ImportNotification(
                            "Library Import",
                            $"Codec could not finish committing '{request.NameHint}'.",
                            ImportNotificationSeverity.Error));
                    }
                    finally
                    {
                        lock (_stateGate)
                        {
                            _processingCount = Math.Max(0, _processingCount - 1);
                            _reservedExecutables.Remove(GetReservationKey(request));
                        }

                        PublishStatus();
                        RaiseCompletionNotificationIfIdle();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
        }

        private async Task<bool> CommitOrSkipAsync(Game game, CancellationToken cancellationToken)
        {
            await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var latestLibrary = await _librarySnapshotProvider().ConfigureAwait(false);
                if (IsDuplicateFinalIdentity(game, latestLibrary))
                {
                    lock (_stateGate) { _skippedCount++; }
                    return false;
                }

                await _commitImportedGameAsync(game).ConfigureAwait(false);
                lock (_stateGate) { _addedCount++; }
                return true;
            }
            finally
            {
                _commitGate.Release();
            }
        }

        private static bool IsDuplicateFinalIdentity(Game candidate, IEnumerable<Game> library)
        {
            bool allowSharedMetadataIdentity = candidate.IsSteamLaunchTarget && candidate.UsesAlternateMetadataLookupName;
            return library.Any(existing =>
                (!string.IsNullOrWhiteSpace(candidate.Executable) &&
                 string.Equals(existing.Executable, candidate.Executable, StringComparison.OrdinalIgnoreCase)) ||
                (candidate.SteamID.HasValue && existing.SteamID == candidate.SteamID) ||
                (!string.IsNullOrWhiteSpace(candidate.EpicAppId) &&
                 string.Equals(existing.EpicAppId, candidate.EpicAppId, StringComparison.OrdinalIgnoreCase)) ||
                RiotGameDuplicateHelper.IsDuplicateGame(candidate.ImportedFrom, candidate.FolderLocation, candidate.LaunchScript, new[] { existing }) ||
                (!allowSharedMetadataIdentity && candidate.IgdbId.HasValue && existing.IgdbId == candidate.IgdbId) ||
                (!allowSharedMetadataIdentity && candidate.RawgID.HasValue && existing.RawgID == candidate.RawgID));
        }

        private static string GetReservationKey(GameImportRequest request)
        {
            if (request.SteamAppId.HasValue)
                return $"steam:{request.SteamAppId.Value}";
            if (!string.IsNullOrWhiteSpace(request.EpicAppId))
                return $"epic:{request.EpicAppId}";
            if (RiotGameDuplicateHelper.IsRiotSource(request.ImportSource) && RiotGameDuplicateHelper.TryGetPathKey(request.LaunchScriptPath, out var riotLaunchTarget))
                return $"launch:{riotLaunchTarget}";
            if (RiotGameDuplicateHelper.IsRiotSource(request.ImportSource) && RiotGameDuplicateHelper.TryGetPathKey(request.FolderLocation, out var riotFolderPath))
                return $"folder:{riotFolderPath}";
            if (!string.IsNullOrWhiteSpace(request.ExecutablePath))
                return request.ExecutablePath;
            return $"name:{request.NameHint}";
        }

        private static string GetReservationKey(ValidatedScanCandidate candidate)
        {
            if (candidate.SteamAppId.HasValue)
                return $"steam:{candidate.SteamAppId.Value}";
            if (!string.IsNullOrWhiteSpace(candidate.EpicAppId))
                return $"epic:{candidate.EpicAppId}";
            if (RiotGameDuplicateHelper.IsRiotSource(candidate.ImportSource) && RiotGameDuplicateHelper.TryGetPathKey(candidate.LaunchScriptPath, out var riotLaunchTarget))
                return $"launch:{riotLaunchTarget}";
            if (RiotGameDuplicateHelper.IsRiotSource(candidate.ImportSource) && RiotGameDuplicateHelper.TryGetPathKey(candidate.FolderLocation, out var riotFolderPath))
                return $"folder:{riotFolderPath}";
            if (!string.IsNullOrWhiteSpace(candidate.ExecutablePath))
                return candidate.ExecutablePath;
            return $"name:{candidate.GameName}";
        }

        private void IncrementSkipped()
        {
            lock (_stateGate)
            {
                _skippedCount++;
            }

            PublishStatus();
        }

        private void RaiseCompletionNotificationIfIdle()
        {
            GameImportStatusSnapshot snapshot = GetSnapshot();
            if (snapshot.IsActive)
            {
                return;
            }

            _idleTcs.TrySetResult(true);

            if (snapshot.AddedCount <= 0 && snapshot.SkippedCount <= 0 && snapshot.FailedCount <= 0)
            {
                if (ScanLogFile.IsSessionActive)
                {
                    ScanLogFile.WriteSummary($"Import results:    {snapshot.AddedCount} added, {snapshot.SkippedCount} skipped, {snapshot.FailedCount} failed");

                    var activeSw = _clickStopwatch;
                    if (activeSw != null && activeSw.IsRunning)
                    {
                        activeSw.Stop();
                        ScanLogFile.WriteSummary($"Total time:       {activeSw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s (scan + pipeline)");
                    }

                    ScanLogFile.EndSession();
                }

                return;
            }

            int sessionTotal = snapshot.AddedCount + snapshot.SkippedCount + snapshot.FailedCount;
            lock (_stateGate)
            {
                if (_lastCompletedSessionTotal == sessionTotal)
                {
                    return;
                }

                _lastCompletedSessionTotal = sessionTotal;
            }

            ScanLogFile.WriteSummary($"Import results:    {snapshot.AddedCount} added, {snapshot.SkippedCount} skipped, {snapshot.FailedCount} failed");

            var sw = _clickStopwatch;
            if (sw != null && sw.IsRunning)
            {
                sw.Stop();
                ScanLogFile.WriteSummary($"Total time:       {sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s (scan + pipeline)");
            }

            ScanLogFile.EndSession();

            RaiseNotification(new ImportNotification(
                "Library Import",
                $"Background import finished: {snapshot.AddedCount} added, {snapshot.SkippedCount} skipped, {snapshot.FailedCount} failed.",
                snapshot.FailedCount > 0 ? ImportNotificationSeverity.Warning : ImportNotificationSeverity.Success));
        }

        private void ResetSessionCountsIfIdle_NoLock()
        {
            if (_isScanRunning || _queuedCount > 0 || _processingCount > 0)
            {
                return;
            }

            _addedCount = 0;
            _skippedCount = 0;
            _failedCount = 0;
            _lastCompletedSessionTotal = 0;
        }

        private GameImportStatusSnapshot GetSnapshot()
        {
            lock (_stateGate)
            {
                string mode = _isScanRunning ? "Scanning and adding games in the background" : "Adding games in the background";
                string message = $"{mode}: {_addedCount} added, {_processingCount} processing, {_queuedCount} queued";

                return new GameImportStatusSnapshot(
                    IsActive: _isScanRunning || _queuedCount > 0 || _processingCount > 0,
                    IsScanning: _isScanRunning,
                    Message: message,
                    QueuedCount: _queuedCount,
                    ProcessingCount: _processingCount,
                    AddedCount: _addedCount,
                    SkippedCount: _skippedCount,
                    FailedCount: _failedCount);
            }
        }

        private void PublishStatus()
        {
            StatusChanged?.Invoke(this, GetSnapshot());
        }

        private void PublishStatus_NoLock()
        {
            StatusChanged?.Invoke(this, GetSnapshot());
        }

        private void RaiseNotification(ImportNotification notification)
        {
            NotificationRaised?.Invoke(this, notification);
        }

        private void RollBackQueuedReservation(string reservationKey)
        {
            lock (_stateGate)
            {
                _queuedCount = Math.Max(0, _queuedCount - 1);
                _reservedExecutables.Remove(reservationKey);
            }

            PublishStatus();
            RaiseCompletionNotificationIfIdle();
        }

        private static TaskCompletionSource<bool> CreateCompletedIdleSource()
        {
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult(true);
            return source;
        }

        public void Dispose()
        {
            _disposeCts.Cancel();
            _scanCts.Cancel();
            _queue.Writer.TryComplete();
            _disposeCts.Dispose();
            _scanCts.Dispose();
        }
    }
}
