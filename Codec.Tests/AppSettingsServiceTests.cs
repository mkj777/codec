using Codec.Services.Storage;
using Xunit;

namespace Codec.Tests
{
    public sealed class AppSettingsServiceTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "codec-storage-tests", Guid.NewGuid().ToString("N"));

        private string SettingsPath => Path.Combine(_directory, "settings.json");

        public AppSettingsServiceTests()
        {
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public async Task SaveThenLoad_RoundTripsTheSettings()
        {
            var service = new AppSettingsService(_directory);

            Assert.True(await service.SaveAsync(new AppSettings { OnboardingCompleted = true, SteamId64 = 42 }));
            var loaded = new AppSettingsService(_directory).Load();

            Assert.True(loaded.OnboardingCompleted);
            Assert.Equal(42UL, loaded.SteamId64);
        }

        [Fact]
        public void Load_WithNoFile_ReportsMissing()
        {
            var service = new AppSettingsService(_directory);

            Assert.False(service.Load().OnboardingCompleted);
            Assert.Equal(StorageLoadOutcome.Missing, service.LastLoadOutcome);
        }

        [Fact]
        public async Task Load_WithTruncatedFile_RecoversFromBackupAndKeepsTheDamagedFile()
        {
            var writer = new AppSettingsService(_directory);
            await writer.SaveAsync(new AppSettings { OnboardingCompleted = true });
            await writer.SaveAsync(new AppSettings { OnboardingCompleted = true, ScanOnStartup = true });
            File.WriteAllText(SettingsPath, "{\"OnboardingComp");

            var service = new AppSettingsService(_directory);
            var loaded = service.Load();

            Assert.Equal(StorageLoadOutcome.RecoveredFromBackup, service.LastLoadOutcome);
            Assert.True(loaded.OnboardingCompleted);
            Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
        }

        [Fact]
        public async Task SyncThenAsyncLoad_OfAnUnreadableFile_BothReportUnreadable()
        {
            // App startup loads settings twice: once for window placement, once in the view model.
            File.WriteAllText(SettingsPath, "");
            var service = new AppSettingsService(_directory);

            service.Load();
            await service.LoadAsync();

            Assert.Equal(StorageLoadOutcome.Unreadable, service.LastLoadOutcome);
        }

        [Fact]
        public async Task Save_SerializesAtCallTime_SoLaterMutationsDoNotLeakIn()
        {
            var service = new AppSettingsService(_directory);
            var settings = new AppSettings { SelectedSortIndex = 1 };

            var save = service.SaveAsync(settings);
            settings.SelectedSortIndex = 2;
            await save;

            Assert.Equal(1, new AppSettingsService(_directory).Load().SelectedSortIndex);
        }

        [Fact]
        public void FireAndForgetSaves_AreAllOnDiskAfterFlush()
        {
            var service = new AppSettingsService(_directory);
            var settings = new AppSettings();

            for (int i = 0; i < 20; i++)
            {
                settings.SelectedSortIndex = i;
                _ = service.SaveAsync(settings);
            }

            Assert.True(service.Flush(TimeSpan.FromSeconds(10)));
            Assert.Equal(19, new AppSettingsService(_directory).Load().SelectedSortIndex);
        }
    }
}
