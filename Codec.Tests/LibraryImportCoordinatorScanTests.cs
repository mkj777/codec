using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Codec.Models;
using Codec.Services.Importing;
using Codec.Services.Logging;
using Codec.Services.Scanning;
using Codec.Services.Scanning.Scanners;
using Xunit;

namespace Codec.Tests
{
    /// <summary>
    /// How a scan's ending reaches the status snapshot. Starting a scan opens a scan log, so these
    /// share the ScanLogFile collection and point the log at a temp folder.
    /// </summary>
    [Collection("ScanLogFile")]
    public sealed class LibraryImportCoordinatorScanTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private readonly string _logBaseDirectory;

        public LibraryImportCoordinatorScanTests()
        {
            _logBaseDirectory = Path.Combine(Path.GetTempPath(), $"codec-scan-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_logBaseDirectory);
            ScanLogFile.SetBaseDirectoryForTests(_logBaseDirectory);
        }

        public void Dispose()
        {
            ScanLogFile.EndSession();
            ScanLogFile.SetBaseDirectoryForTests(null);
            try
            {
                Directory.Delete(_logBaseDirectory, recursive: true);
            }
            catch
            {
                // best effort for temp files
            }
        }

        [Fact]
        public async Task CleanScan_EndsCompleted()
        {
            var scanner = new FakeScanSource((sourceFailed, ct) => FakeScanSource.Yield(Candidates("a", "b"), ct));
            var pipeline = new FakePipeline(AddGame);
            var (coordinator, snapshots) = Create(pipeline, scanner);
            using (coordinator)
            {
                await RunScanAsync(coordinator);

                var final = snapshots.Last(s => !s.IsActive);
                Assert.Equal(ScanOutcome.Completed, final.Outcome);
                Assert.Equal(2, final.AddedCount);
                Assert.Equal(0, final.FailedCount);
            }

            // The end of a scan is announced once, with its outcome. An idle snapshot without
            // one would be read as the scan's end before the outcome was known.
            Assert.All(snapshots.Where(s => !s.IsActive), s => Assert.NotEqual(ScanOutcome.None, s.Outcome));
        }

        [Fact]
        public async Task ScannerThrowsMidScan_EndsStopped()
        {
            var scanner = new FakeScanSource((sourceFailed, ct) =>
                FakeScanSource.Yield(Candidates("a"), ct, throwAfter: new IOException("drive went away")));
            var pipeline = new FakePipeline(AddGame);
            var (coordinator, snapshots) = Create(pipeline, scanner);
            using (coordinator)
            {
                await RunScanAsync(coordinator);

                var final = snapshots.Last(s => !s.IsActive);
                Assert.Equal(ScanOutcome.Stopped, final.Outcome);
                Assert.Equal(1, final.AddedCount);
            }
        }

        [Fact]
        public async Task ItemsFailInsideFinishedScan_EndsIncomplete()
        {
            var scanner = new FakeScanSource((sourceFailed, ct) => FakeScanSource.Yield(Candidates("a", "b", "c"), ct));
            var pipeline = new FakePipeline(request =>
                request.NameHint == "b" ? GameImportResult.Failed("artwork download failed") : AddGame(request));
            var (coordinator, snapshots) = Create(pipeline, scanner);
            using (coordinator)
            {
                await RunScanAsync(coordinator);

                var final = snapshots.Last(s => !s.IsActive);
                Assert.Equal(ScanOutcome.Incomplete, final.Outcome);
                Assert.Equal(2, final.AddedCount);
                Assert.Equal(1, final.FailedCount);
            }
        }

        [Fact]
        public async Task SourceThatCouldNotBeChecked_EndsIncomplete()
        {
            var scanner = new FakeScanSource((sourceFailed, ct) =>
                FakeScanSource.Yield(Candidates("a"), ct, beforeYield: () => sourceFailed?.Invoke("Epic Games")));
            var pipeline = new FakePipeline(AddGame);
            var (coordinator, snapshots) = Create(pipeline, scanner);
            using (coordinator)
            {
                await RunScanAsync(coordinator);

                var final = snapshots.Last(s => !s.IsActive);
                Assert.Equal(ScanOutcome.Incomplete, final.Outcome);
                Assert.Equal(0, final.FailedCount);
            }
        }

        [Fact]
        public async Task NextScan_StartsFromACleanOutcome()
        {
            int run = 0;
            var scanner = new FakeScanSource((sourceFailed, ct) =>
                Interlocked.Increment(ref run) == 1
                    ? FakeScanSource.Yield(Candidates("a"), ct, throwAfter: new InvalidOperationException("boom"))
                    : FakeScanSource.Yield(Candidates("b"), ct));
            var pipeline = new FakePipeline(AddGame);
            var (coordinator, snapshots) = Create(pipeline, scanner);
            using (coordinator)
            {
                await RunScanAsync(coordinator);
                Assert.Equal(ScanOutcome.Stopped, snapshots.Last(s => !s.IsActive).Outcome);

                await RunScanAsync(coordinator);
                Assert.Equal(ScanOutcome.Completed, snapshots.Last(s => !s.IsActive).Outcome);
            }
        }

        [Fact]
        public async Task CancelWithQueuedItems_EndsCancelled_AndFreesTheNextScan()
        {
            int run = 0;
            var scanner = new FakeScanSource((sourceFailed, ct) =>
                Interlocked.Increment(ref run) == 1
                    ? FakeScanSource.Yield(Candidates("a", "b", "c", "d", "e", "f"), ct)
                    : FakeScanSource.Yield(Candidates("z"), ct));
            var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var saturated = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int active = 0;
            var pipeline = new FakePipeline(async request =>
            {
                if (Interlocked.Increment(ref active) == 2) saturated.TrySetResult(null);
                await release.Task;
                return AddGame(request);
            });
            var (coordinator, snapshots) = Create(pipeline, scanner);
            int alreadyRunningWarnings = 0;
            coordinator.NotificationRaised += (_, n) =>
            {
                if (n.Severity == ImportNotificationSeverity.Warning && !n.IsManual)
                    Interlocked.Increment(ref alreadyRunningWarnings);
            };

            using (coordinator)
            {
                try
                {
                    await coordinator.StartScanAsync();
                    await saturated.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
                    coordinator.Cancel();
                    release.TrySetResult(null);
                    await coordinator.WaitForIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

                    Assert.Equal(ScanOutcome.Cancelled, snapshots.Last(s => !s.IsActive).Outcome);

                    await RunScanAsync(coordinator);

                    Assert.Equal(0, Volatile.Read(ref alreadyRunningWarnings));
                    Assert.Equal(ScanOutcome.Completed, snapshots.Last(s => !s.IsActive).Outcome);
                }
                finally
                {
                    release.TrySetResult(null);
                }
            }
        }

        [Fact]
        public async Task PlatformScannerThatThrows_IsReportedAsUnchecked()
        {
            var failed = new List<string>();

            var result = await GameScanner.ScanPlatformScannerAsync(
                new ThrowingPlatformScanner(new UnauthorizedAccessException("registry")),
                progress: null,
                sourceFailed: failed.Add,
                CancellationToken.None);

            Assert.Empty(result.Candidates);
            Assert.Equal(new[] { "Throwing" }, failed);
        }

        [Fact]
        public async Task PlatformScanner_CancelledByTheScan_IsNotReportedAsUnchecked()
        {
            var failed = new List<string>();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameScanner.ScanPlatformScannerAsync(
                new ThrowingPlatformScanner(new OperationCanceledException()),
                progress: null,
                sourceFailed: failed.Add,
                cts.Token));

            Assert.Empty(failed);
        }

        private static async Task RunScanAsync(LibraryImportCoordinator coordinator)
        {
            await coordinator.StartScanAsync();
            await coordinator.WaitForIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        private static (LibraryImportCoordinator Coordinator, ConcurrentQueue<GameImportStatusSnapshot> Snapshots) Create(
            IGameImportPipeline pipeline,
            IGameScanSource scanner)
        {
            var library = new List<Game>();
            var snapshots = new ConcurrentQueue<GameImportStatusSnapshot>();
            var coordinator = new LibraryImportCoordinator(
                pipeline,
                scanner,
                () =>
                {
                    lock (library) return Task.FromResult<IReadOnlyCollection<Game>>(library.ToList());
                },
                game =>
                {
                    lock (library) library.Add(game);
                    return Task.CompletedTask;
                },
                postScanMaintenanceAsync: null,
                new ScanConcurrencyOptions(2, 2, 2, 2, 1, 1, 8, 4));
            coordinator.StatusChanged += (_, snapshot) => snapshots.Enqueue(snapshot);
            return (coordinator, snapshots);
        }

        private static GameImportResult AddGame(GameImportRequest request) =>
            GameImportResult.Added(new Game
            {
                Name = request.NameHint,
                Executable = request.ExecutablePath,
                FolderLocation = request.FolderLocation,
                ImportedFrom = request.ImportSource,
                IsFullyImported = true
            }, "added");

        // Paths are never opened: the fake pipeline stands in for everything that would.
        private static IReadOnlyList<ValidatedScanCandidate> Candidates(params string[] names) =>
            names.Select(name => new ValidatedScanCandidate(
                SteamAppId: null,
                GameName: name,
                RawgId: null,
                ImportSource: "Heuristic Scan",
                ExecutablePath: Path.Combine(Path.GetTempPath(), "codec-scan-tests", name, $"{name}.exe"),
                FolderLocation: Path.Combine(Path.GetTempPath(), "codec-scan-tests", name)))
            .ToList();

        private sealed class ThrowingPlatformScanner : PlatformScanner
        {
            private readonly Exception _exception;

            public ThrowingPlatformScanner(Exception exception) => _exception = exception;

            public override string PlatformName => "Throwing";

            public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
                Task.FromException<List<GameCandidate>>(_exception);
        }
    }

    /// <summary>A scan whose candidates, source failures and crash the test decides.</summary>
    internal sealed class FakeScanSource : IGameScanSource
    {
        private readonly Func<Action<string>?, CancellationToken, IAsyncEnumerable<ValidatedScanCandidate>> _scan;

        public FakeScanSource(Func<Action<string>?, CancellationToken, IAsyncEnumerable<ValidatedScanCandidate>> scan)
        {
            _scan = scan;
        }

        public static FakeScanSource Empty => new((_, ct) => Yield(Array.Empty<ValidatedScanCandidate>(), ct));

        public string? DetectedSteamClientPath => null;
        public string? DetectedEpicLauncherPath => null;

        public IAsyncEnumerable<ValidatedScanCandidate> ScanIncrementallyAsync(
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null,
            Stopwatch? clickStopwatch = null,
            Action<string>? sourceFailed = null) =>
            _scan(sourceFailed, cancellationToken);

        public static async IAsyncEnumerable<ValidatedScanCandidate> Yield(
            IReadOnlyList<ValidatedScanCandidate> candidates,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            Exception? throwAfter = null,
            Action? beforeYield = null)
        {
            beforeYield?.Invoke();
            foreach (var candidate in candidates)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return candidate;
            }

            if (throwAfter != null)
                throw throwAfter;
        }
    }
}
