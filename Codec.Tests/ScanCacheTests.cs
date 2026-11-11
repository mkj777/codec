using Codec.Services.Scanning;
using Codec.Services.Scanning.Scanners;
using Xunit;

namespace Codec.Tests
{
    public sealed class ScanCacheTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"codec-scan-cache-{Guid.NewGuid():N}");
        private readonly string _cachePath;

        public ScanCacheTests()
        {
            Directory.CreateDirectory(_root);
            _cachePath = Path.Combine(_root, "scan-cache.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public async Task SavedEntries_SurviveAReload_AndLeaveNoTempFileBehind()
        {
            var candidate = CreateSteamCandidate();
            var cache = await ScanCache.LoadAsync(_cachePath);
            cache.Upsert(candidate, candidate.Name, string.Empty, candidate.SteamAppId, null, null);

            await cache.SaveAsync();
            var reloaded = await ScanCache.LoadAsync(_cachePath);

            Assert.True(reloaded.TryGetValid(candidate, out var entry));
            Assert.Equal(candidate.SteamAppId, entry.SteamAppId);
            Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("[{\"FolderPath\":")]
        [InlineData("null")]
        public async Task UnreadableCache_StartsEmpty_AndCanBeSavedAgain(string content)
        {
            File.WriteAllText(_cachePath, content);
            var candidate = CreateSteamCandidate();

            var cache = await ScanCache.LoadAsync(_cachePath);

            Assert.False(cache.TryGetValid(candidate, out _));
            cache.Upsert(candidate, candidate.Name, string.Empty, candidate.SteamAppId, null, null);
            await cache.SaveAsync();
            Assert.True((await ScanCache.LoadAsync(_cachePath)).TryGetValid(candidate, out _));
        }

        [Fact]
        public async Task MissingCache_StartsEmpty()
        {
            var cache = await ScanCache.LoadAsync(_cachePath);

            Assert.False(cache.TryGetValid(CreateSteamCandidate(), out _));
        }

        private GameCandidate CreateSteamCandidate()
        {
            string folder = Path.Combine(_root, "Portal");
            Directory.CreateDirectory(folder);
            return new GameCandidate("Portal", folder, "Steam", SteamAppId: 400);
        }
    }
}
