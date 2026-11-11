using Codec.Models;
using Codec.Services.Storage;
using System.Text;
using Xunit;

namespace Codec.Tests
{
    public sealed class LibraryStorageServiceTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "codec-storage-tests", Guid.NewGuid().ToString("N"));

        private string LibraryPath => Path.Combine(_directory, "library.json");
        private string BackupPath => LibraryPath + ".bak";

        public LibraryStorageServiceTests()
        {
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        private static Game NewGame(string name, bool favorite = false) => new() { Name = name, IsFavorite = favorite };

        private string[] CorruptCopies(string fileName) => Directory.GetFiles(_directory, fileName + ".corrupt-*");

        [Fact]
        public async Task SaveThenLoad_RoundTripsTheLibrary()
        {
            var service = new LibraryStorageService(_directory);

            Assert.True(await service.SaveAsync([NewGame("Hades", favorite: true), NewGame("Celeste")]));
            var loaded = await new LibraryStorageService(_directory).LoadAsync();

            Assert.Equal(["Hades", "Celeste"], loaded.Select(game => game.Name));
            Assert.True(loaded[0].IsFavorite);
        }

        [Fact]
        public async Task Load_WithNoFile_ReportsMissing()
        {
            var service = new LibraryStorageService(_directory);

            var loaded = await service.LoadAsync();

            Assert.Empty(loaded);
            Assert.Equal(StorageLoadOutcome.Missing, service.LastLoadOutcome);
        }

        [Fact]
        public async Task SecondSave_KeepsThePreviousGenerationAsBackup_AndLeavesNoTempFile()
        {
            var service = new LibraryStorageService(_directory);

            await service.SaveAsync([NewGame("First")]);
            await service.SaveAsync([NewGame("Second")]);

            Assert.Contains("First", File.ReadAllText(BackupPath));
            Assert.Contains("Second", File.ReadAllText(LibraryPath));
            Assert.False(File.Exists(LibraryPath + ".tmp"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("[{\"Name\":\"Hal")]
        [InlineData("null")]
        public async Task Load_WithDamagedFileAndGoodBackup_RecoversAndPreservesTheDamagedFile(string damaged)
        {
            var writer = new LibraryStorageService(_directory);
            await writer.SaveAsync([NewGame("Backup")]);
            await writer.SaveAsync([NewGame("Newest")]);
            File.WriteAllText(LibraryPath, damaged);

            var service = new LibraryStorageService(_directory);
            var loaded = await service.LoadAsync();

            Assert.Equal(StorageLoadOutcome.RecoveredFromBackup, service.LastLoadOutcome);
            Assert.Equal("Backup", Assert.Single(loaded).Name);
            string preserved = Assert.Single(CorruptCopies("library.json"));
            Assert.Equal(damaged, File.ReadAllText(preserved));
        }

        [Fact]
        public async Task SaveAfterRecovery_NeverRotatesTheDamagedFileIntoTheBackup()
        {
            var writer = new LibraryStorageService(_directory);
            await writer.SaveAsync([NewGame("Backup")]);
            await writer.SaveAsync([NewGame("Newest")]);
            File.WriteAllText(LibraryPath, "");

            var service = new LibraryStorageService(_directory);
            await service.LoadAsync();
            Assert.True(await service.SaveAsync([NewGame("AfterRecovery")]));

            Assert.Contains("Backup", File.ReadAllText(BackupPath));
            Assert.Contains("AfterRecovery", File.ReadAllText(LibraryPath));
        }

        [Fact]
        public async Task SaveWithoutLoad_SetsADamagedTargetAsideInsteadOfBackingItUp()
        {
            var writer = new LibraryStorageService(_directory);
            await writer.SaveAsync([NewGame("Backup")]);
            await writer.SaveAsync([NewGame("Newest")]);
            File.WriteAllText(LibraryPath, "[{\"Na");

            Assert.True(await new LibraryStorageService(_directory).SaveAsync([NewGame("Fresh")]));

            Assert.Contains("Backup", File.ReadAllText(BackupPath));
            Assert.Contains("Fresh", File.ReadAllText(LibraryPath));
            Assert.Single(CorruptCopies("library.json"));
        }

        [Fact]
        public async Task Load_WithOnlyABackup_RecoversInsteadOfReportingAFreshInstall()
        {
            // What a crash between the two renames inside File.Replace leaves behind.
            var writer = new LibraryStorageService(_directory);
            await writer.SaveAsync([NewGame("Only")]);
            await writer.SaveAsync([NewGame("Lost")]);
            File.Delete(LibraryPath);

            var service = new LibraryStorageService(_directory);
            var loaded = await service.LoadAsync();

            Assert.Equal(StorageLoadOutcome.RecoveredFromBackup, service.LastLoadOutcome);
            Assert.Equal("Only", Assert.Single(loaded).Name);
        }

        [Fact]
        public async Task Load_WithDamagedFileAndNoBackup_ReportsUnreadableAndKeepsTheFile()
        {
            File.WriteAllText(LibraryPath, "[{\"Name\":\"Trunc");

            var service = new LibraryStorageService(_directory);
            var loaded = await service.LoadAsync();

            Assert.Empty(loaded);
            Assert.Equal(StorageLoadOutcome.Unreadable, service.LastLoadOutcome);
            string preserved = Assert.Single(CorruptCopies("library.json"));

            Assert.True(await service.SaveAsync([NewGame("Rebuilt")]));
            Assert.Equal("[{\"Name\":\"Trunc", File.ReadAllText(preserved));
        }

        [Fact]
        public async Task Load_WithDamagedFileAndDamagedBackup_PreservesBoth()
        {
            File.WriteAllText(LibraryPath, "{");
            File.WriteAllText(BackupPath, "");

            var service = new LibraryStorageService(_directory);
            await service.LoadAsync();
            await service.SaveAsync([NewGame("One")]);
            await service.SaveAsync([NewGame("Two")]);

            Assert.Equal(StorageLoadOutcome.Unreadable, service.LastLoadOutcome);
            Assert.Single(CorruptCopies("library.json"));
            Assert.Single(CorruptCopies("library.json.bak"));
        }

        [Fact]
        public async Task Load_WhenTheDamagedFileCannotBeSetAside_RefusesToOverwriteIt()
        {
            File.WriteAllText(LibraryPath, "{");
            var service = new LibraryStorageService(_directory);

            // Read sharing lets the load see the file but blocks the rename.
            using (new FileStream(LibraryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await service.LoadAsync();
            }

            Assert.Equal(StorageLoadOutcome.Unreadable, service.LastLoadOutcome);
            Assert.False(await service.SaveAsync([NewGame("Empty shelf")]));
            Assert.NotNull(service.LastSaveError);
            Assert.Equal("{", File.ReadAllText(LibraryPath));
        }

        [Fact]
        public async Task Load_WhenTheDamagedFileCannotBeSetAside_StillUsesTheBackupButWritesNothing()
        {
            var writer = new LibraryStorageService(_directory);
            await writer.SaveAsync([NewGame("Backup")]);
            await writer.SaveAsync([NewGame("Newest")]);
            File.WriteAllText(LibraryPath, "{");
            var service = new LibraryStorageService(_directory);

            List<Game> loaded;
            using (new FileStream(LibraryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                loaded = await service.LoadAsync();
            }

            Assert.Equal(StorageLoadOutcome.RecoveredFromBackup, service.LastLoadOutcome);
            Assert.Equal("Backup", Assert.Single(loaded).Name);
            Assert.False(await service.SaveAsync(loaded));
            Assert.Equal("{", File.ReadAllText(LibraryPath));
            Assert.Contains("Backup", File.ReadAllText(BackupPath));
        }

        [Fact]
        public async Task OverlappingSaves_LeaveTheNewestStateOnDisk()
        {
            var service = new LibraryStorageService(_directory);

            var saves = Enumerable.Range(1, 25)
                .Select(i => service.SaveAsync([NewGame($"Game {i}")]))
                .ToList();
            await service.FlushAsync();

            Assert.All(saves, save => Assert.True(save.IsCompletedSuccessfully && save.Result));
            Assert.Equal("Game 25", Assert.Single(await new LibraryStorageService(_directory).LoadAsync()).Name);
        }

        [Fact]
        public async Task Save_SnapshotsTheGamesAtCallTime()
        {
            var service = new LibraryStorageService(_directory);
            var game = NewGame("Before");

            var save = service.SaveAsync([game]);
            game.Name = "After";
            await save;

            Assert.Equal("Before", Assert.Single(await new LibraryStorageService(_directory).LoadAsync()).Name);
        }

        [Fact]
        public async Task Save_WhenTheTargetIsLocked_ReportsFailureAndKeepsTheFile()
        {
            var service = new LibraryStorageService(_directory);
            await service.SaveAsync([NewGame("Kept")]);
            int failures = 0;
            service.SaveFailed += (_, _) => Interlocked.Increment(ref failures);

            bool saved;
            using (new FileStream(LibraryPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                saved = await service.SaveAsync([NewGame("Blocked")]);
            }

            Assert.False(saved);
            Assert.NotNull(service.LastSaveError);
            Assert.Equal(1, failures);
            Assert.Contains("Kept", File.ReadAllText(LibraryPath, Encoding.UTF8));

            Assert.True(await service.SaveAsync([NewGame("Later")]));
            Assert.Null(service.LastSaveError);
        }

        [Fact]
        public void Flush_WithNothingPending_ReturnsImmediately()
        {
            Assert.True(new LibraryStorageService(_directory).Flush(TimeSpan.Zero));
        }
    }
}
