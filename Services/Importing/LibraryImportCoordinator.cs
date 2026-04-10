using Codec.Models;
using Codec.Services.Scanning;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private readonly Channel<GameImportRequest> _queue;
        private readonly HashSet<string> _reservedExecutables = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _stateGate = new();
        private readonly CancellationTokenSource _disposeCts = new();

        private bool _isScanRunning;
        private int _queuedCount;
        private int _processingCount;
        private int _addedCount;
        private int _skippedCount;
        private int _failedCount;
        private int _lastCompletedSessionTotal;

        public event EventHandler<GameImportStatusSnapshot>? StatusChanged;
        public event EventHandler<ImportNotification>? NotificationRaised;

        public LibraryImportCoordinator(
            IGameImportPipeline pipeline,
            GameScanner scanner,
            Func<Task<IReadOnlyCollection<Game>>> librarySnapshotProvider,
            Func<Game, Task> commitImportedGameAsync)
        {
            _pipeline = pipeline;
            _scanner = scanner;
            _librarySnapshotProvider = librarySnapshotProvider;
            _commitImportedGameAsync = commitImportedGameAsync;
            _queue = Channel.CreateUnbounded<GameImportRequest>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            _ = ProcessQueueAsync();
        }

        public async Task StartScanAsync()
        {
            lock (_stateGate)
            {
                if (_isScanRunning)
                {
                    RaiseNotification(new ImportNotification(
                        "Library Import",
                        "A scan is already running in the background.",
                        ImportNotificationSeverity.Warning));
                    return;
                }

                ResetSessionCountsIfIdle_NoLock();
                _isScanRunning = true;
            }

            PublishStatus();
            RaiseNotification(new ImportNotification(
                "Library Import",
                "Scanning for games in the background.",
                ImportNotificationSeverity.Informational));

            _ = RunScanAsync();
            await Task.CompletedTask;
        }

        private async Task RunScanAsync()
        {
            try
            {
                await foreach (var candidate in _scanner.ScanIncrementallyAsync(_disposeCts.Token).ConfigureAwait(false))
                {
                    await TryEnqueueScanCandidateAsync(candidate).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Background scan failed: {ex.Message}");
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
                ResetSessionCountsIfIdle_NoLock();
                if (!_reservedExecutables.Add(normalizedPath))
                {
                    return new ImportEnqueueResult(ImportEnqueueResultStatus.Duplicate, "This executable is already queued for import.");
                }

                _queuedCount++;
            }

            string folder = Path.GetDirectoryName(normalizedPath) ?? string.Empty;
            string nameHint = Path.GetFileNameWithoutExtension(normalizedPath);
            await _queue.Writer.WriteAsync(new GameImportRequest(
                normalizedPath,
                folder,
                nameHint,
                "Manual Executable",
                IsManual: true), _disposeCts.Token).ConfigureAwait(false);

            PublishStatus();
            return new ImportEnqueueResult(ImportEnqueueResultStatus.Accepted, "Game queued for background import.");
        }

        private async Task TryEnqueueScanCandidateAsync(ValidatedScanCandidate candidate)
        {
            var librarySnapshot = await _librarySnapshotProvider().ConfigureAwait(false);

            if (librarySnapshot.Any(g => string.Equals(g.Executable, candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            {
                IncrementSkipped();
                return;
            }

            lock (_stateGate)
            {
                ResetSessionCountsIfIdle_NoLock();
                if (!_reservedExecutables.Add(candidate.ExecutablePath))
                {
                    _skippedCount++;
                    PublishStatus_NoLock();
                    return;
                }

                _queuedCount++;
            }

            await _queue.Writer.WriteAsync(new GameImportRequest(
                candidate.ExecutablePath,
                candidate.FolderLocation,
                candidate.GameName,
                candidate.ImportSource,
                candidate.SteamAppId,
                candidate.RawgId,
                IsManual: false), _disposeCts.Token).ConfigureAwait(false);

            PublishStatus();
        }

        private async Task ProcessQueueAsync()
        {
            try
            {
                await foreach (var request in _queue.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
                {
                    lock (_stateGate)
                    {
                        _queuedCount = Math.Max(0, _queuedCount - 1);
                        _processingCount = 1;
                    }

                    PublishStatus();
                    var librarySnapshot = await _librarySnapshotProvider().ConfigureAwait(false);
                    var result = await _pipeline.ImportAsync(request, librarySnapshot, _disposeCts.Token).ConfigureAwait(false);

                    try
                    {
                        switch (result.Status)
                        {
                            case GameImportResultStatus.Added when result.Game != null && result.Game.IsFullyImported && result.Game.DisplayedAssetsReady:
                                await _commitImportedGameAsync(result.Game).ConfigureAwait(false);
                                lock (_stateGate)
                                {
                                    _addedCount++;
                                }

                                if (request.IsManual)
                                {
                                    RaiseNotification(new ImportNotification(
                                        "Library Import",
                                        result.Message,
                                        ImportNotificationSeverity.Success));
                                }
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
                                    ImportNotificationSeverity.Error));
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
                                        ImportNotificationSeverity.Warning));
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
                                    ImportNotificationSeverity.Error));
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Commit failed for '{request.ExecutablePath}': {ex.Message}");
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
                            _processingCount = 0;
                            _reservedExecutables.Remove(request.ExecutablePath);
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

            if (snapshot.AddedCount <= 0 && snapshot.SkippedCount <= 0 && snapshot.FailedCount <= 0)
            {
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

        public void Dispose()
        {
            _disposeCts.Cancel();
            _queue.Writer.TryComplete();
            _disposeCts.Dispose();
        }
    }
}
