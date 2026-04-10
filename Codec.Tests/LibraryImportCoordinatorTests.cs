using Codec.Models;
using Codec.Services.Importing;
using Codec.Services.Resolving;
using Codec.Services.Scanning;
using Codec.Services.Storage;
using Xunit;

namespace Codec.Tests
{
    public sealed class LibraryImportCoordinatorTests
    {
        [Fact]
        public async Task EnqueueManualExecutableAsync_ReturnsInvalid_ForMissingFile()
        {
            var pipeline = new FakePipeline(_ => GameImportResult.Invalid("invalid"));
            using var coordinator = CreateCoordinator(pipeline);

            var result = await coordinator.EnqueueManualExecutableAsync(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exe"));

            Assert.Equal(ImportEnqueueResultStatus.Invalid, result.Status);
            Assert.Equal(0, pipeline.CallCount);
        }

        [Fact]
        public async Task EnqueueManualExecutableAsync_ReturnsDuplicate_ForExistingLibraryExecutable()
        {
            string exePath = CreateTempExe();
            try
            {
                var existing = new Game { Name = "Existing", Executable = exePath, FolderLocation = Path.GetDirectoryName(exePath) ?? string.Empty, ImportedFrom = "Manual Executable" };
                var pipeline = new FakePipeline(_ => GameImportResult.Invalid("should not run"));
                using var coordinator = CreateCoordinator(
                    pipeline,
                    () => Task.FromResult<IReadOnlyCollection<Game>>(new[] { existing }));

                var result = await coordinator.EnqueueManualExecutableAsync(exePath);

                Assert.Equal(ImportEnqueueResultStatus.Duplicate, result.Status);
                Assert.Equal(0, pipeline.CallCount);
            }
            finally
            {
                DeleteIfExists(exePath);
            }
        }

        [Fact]
        public async Task EnqueueManualExecutableAsync_ReturnsAccepted_ForNewExecutable()
        {
            string exePath = CreateTempExe();
            string[] assetPaths = Array.Empty<string>();
            var committed = new TaskCompletionSource<Game>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                var pipeline = new FakePipeline(request =>
                {
                    var game = CreateReadyImportedGame(request, out assetPaths);
                    return GameImportResult.Added(game, "added");
                });

                using var coordinator = CreateCoordinator(
                    pipeline,
                    commitImportedGameAsync: game =>
                    {
                        committed.TrySetResult(game);
                        return Task.CompletedTask;
                    });

                var result = await coordinator.EnqueueManualExecutableAsync(exePath);
                var imported = await committed.Task.WaitAsync(TimeSpan.FromSeconds(3));

                Assert.Equal(ImportEnqueueResultStatus.Accepted, result.Status);
                Assert.Equal(exePath, imported.Executable);
            }
            finally
            {
                DeleteIfExists(exePath);
                DeleteIfExists(assetPaths);
            }
        }

        [Fact]
        public async Task EnqueueManualExecutableAsync_SuppressesDuplicateWhileInFlight()
        {
            string exePath = CreateTempExe();
            var releaseImport = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                var pipeline = new FakePipeline(async request =>
                {
                    await releaseImport.Task;
                    return GameImportResult.Added(new Game
                    {
                        Name = request.NameHint,
                        Executable = request.ExecutablePath,
                        FolderLocation = request.FolderLocation,
                        ImportedFrom = request.ImportSource,
                        IsFullyImported = true
                    }, "added");
                });

                using var coordinator = CreateCoordinator(pipeline);

                var first = await coordinator.EnqueueManualExecutableAsync(exePath);
                var second = await coordinator.EnqueueManualExecutableAsync(exePath);
                releaseImport.TrySetResult(null);

                Assert.Equal(ImportEnqueueResultStatus.Accepted, first.Status);
                Assert.Equal(ImportEnqueueResultStatus.Duplicate, second.Status);
            }
            finally
            {
                DeleteIfExists(exePath);
            }
        }

        [Fact]
        public async Task Coordinator_CommitsOnlyFullyImportedGames()
        {
            string exePath = CreateTempExe();
            string[] assetPaths = Array.Empty<string>();
            var commitSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                var pipeline = new FakePipeline(request =>
                {
                    var game = CreateReadyImportedGame(request, out assetPaths);
                    return GameImportResult.Added(game, "added");
                });

                using var coordinator = CreateCoordinator(
                    pipeline,
                    commitImportedGameAsync: game =>
                    {
                        commitSeen.TrySetResult(game.IsFullyImported);
                        return Task.CompletedTask;
                    });

                await coordinator.EnqueueManualExecutableAsync(exePath);
                bool wasFullyImported = await commitSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));

                Assert.True(wasFullyImported);
            }
            finally
            {
                DeleteIfExists(exePath);
                DeleteIfExists(assetPaths);
            }
        }

        [Fact]
        public async Task Coordinator_ContinuesProcessingAfterFailure()
        {
            string firstExe = CreateTempExe();
            string secondExe = CreateTempExe();
            string[] assetPaths = Array.Empty<string>();
            var commitSeen = new TaskCompletionSource<Game>(TaskCreationOptions.RunContinuationsAsynchronously);
            int callCount = 0;

            try
            {
                var pipeline = new FakePipeline(request =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        return GameImportResult.Failed("boom");
                    }

                    var game = CreateReadyImportedGame(request, out assetPaths);
                    return GameImportResult.Added(game, "added");
                });

                using var coordinator = CreateCoordinator(
                    pipeline,
                    commitImportedGameAsync: game =>
                    {
                        commitSeen.TrySetResult(game);
                        return Task.CompletedTask;
                    });

                await coordinator.EnqueueManualExecutableAsync(firstExe);
                await coordinator.EnqueueManualExecutableAsync(secondExe);
                var committedGame = await commitSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));

                Assert.Equal(secondExe, committedGame.Executable);
                Assert.Equal(2, pipeline.CallCount);
            }
            finally
            {
                DeleteIfExists(firstExe);
                DeleteIfExists(secondExe);
                DeleteIfExists(assetPaths);
            }
        }

        [Fact]
        public async Task Coordinator_DoesNotCommitAddedGame_WhenDisplayedAssetsAreNotReady()
        {
            string exePath = CreateTempExe();
            bool wasCommitted = false;

            try
            {
                var pipeline = new FakePipeline(request =>
                    GameImportResult.Added(new Game
                    {
                        Name = request.NameHint,
                        Executable = request.ExecutablePath,
                        FolderLocation = request.FolderLocation,
                        ImportedFrom = request.ImportSource,
                        IsFullyImported = true,
                        HasHeroAssetSource = true,
                        HasLogoAssetSource = true
                    }, "added"));

                using var coordinator = CreateCoordinator(
                    pipeline,
                    commitImportedGameAsync: game =>
                    {
                        wasCommitted = true;
                        return Task.CompletedTask;
                    });

                await coordinator.EnqueueManualExecutableAsync(exePath);
                await Task.Delay(150);

                Assert.False(wasCommitted);
            }
            finally
            {
                DeleteIfExists(exePath);
            }
        }

        private static LibraryImportCoordinator CreateCoordinator(
            IGameImportPipeline pipeline,
            Func<Task<IReadOnlyCollection<Game>>>? librarySnapshotProvider = null,
            Func<Game, Task>? commitImportedGameAsync = null)
        {
            var cache = new MetadataCache();
            var gameDetails = new GameDetailsService(cache);
            var gameName = new GameNameService(gameDetails);
            var scanner = new GameScanner(gameName);

            return new LibraryImportCoordinator(
                pipeline,
                scanner,
                librarySnapshotProvider ?? (() => Task.FromResult<IReadOnlyCollection<Game>>(Array.Empty<Game>())),
                commitImportedGameAsync ?? (_ => Task.CompletedTask));
        }

        private static string CreateTempExe()
        {
            string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exe");
            File.WriteAllText(path, "stub");
            return path;
        }

        private static Game CreateReadyImportedGame(GameImportRequest request, out string[] assetPaths)
        {
            string coverPath = CreateTempAsset(".png");
            string heroPath = CreateTempAsset(".png");
            assetPaths = new[] { coverPath, heroPath };

            return new Game
            {
                Name = request.NameHint,
                Executable = request.ExecutablePath,
                FolderLocation = request.FolderLocation,
                ImportedFrom = request.ImportSource,
                IsFullyImported = true,
                LibCapsuleCache = coverPath,
                LibHeroCache = heroPath,
                HasLogoAssetSource = false
            };
        }

        private static string CreateTempAsset(string extension)
        {
            string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
            File.WriteAllText(path, "asset");
            return path;
        }

        private static void DeleteIfExists(params string[] paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // best effort for temp files
                }
            }
        }

        private sealed class FakePipeline : IGameImportPipeline
        {
            private readonly Func<GameImportRequest, Task<GameImportResult>> _handler;

            public FakePipeline(Func<GameImportRequest, GameImportResult> handler)
                : this(request => Task.FromResult(handler(request)))
            {
            }

            public FakePipeline(Func<GameImportRequest, Task<GameImportResult>> handler)
            {
                _handler = handler;
            }

            public int CallCount { get; private set; }

            public async Task<GameImportResult> ImportAsync(GameImportRequest request, IReadOnlyCollection<Game> librarySnapshot, CancellationToken cancellationToken = default)
            {
                CallCount++;
                return await _handler(request);
            }
        }
    }
}
