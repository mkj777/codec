using Codec.Services.Scanning.Scanners;
using Xunit;

namespace Codec.Tests
{
    public sealed class RiotGamesScannerTests
    {
        [Fact]
        public async Task ScanAsync_FindsGamesAcrossMultipleRiotRoots()
        {
            string tempRoot = CreateTempDirectory();

            try
            {
                string firstRiotRoot = Path.Combine(tempRoot, "C", "Riot Games");
                string secondRiotRoot = Path.Combine(tempRoot, "D", "Riot Games");
                Directory.CreateDirectory(Path.Combine(firstRiotRoot, "League of Legends"));
                Directory.CreateDirectory(Path.Combine(firstRiotRoot, "Riot Client"));
                Directory.CreateDirectory(Path.Combine(secondRiotRoot, "VALORANT"));

                string missingStartMenuPath = Path.Combine(tempRoot, "missing-start-menu");
                var scanner = new RiotGamesScanner(
                    () => new[] { firstRiotRoot, secondRiotRoot },
                    missingStartMenuPath);

                var candidates = await scanner.ScanAsync();

                Assert.Contains(candidates, candidate =>
                    candidate.Name == "League of Legends" &&
                    candidate.FolderPath == Path.Combine(firstRiotRoot, "League of Legends"));
                Assert.Contains(candidates, candidate =>
                    candidate.Name == "VALORANT" &&
                    candidate.FolderPath == Path.Combine(secondRiotRoot, "VALORANT"));
                Assert.DoesNotContain(candidates, candidate => candidate.Name == "Riot Client");
                Assert.Contains(firstRiotRoot, scanner.KnownLibraryPaths);
                Assert.Contains(secondRiotRoot, scanner.KnownLibraryPaths);
            }
            finally
            {
            }
        }

        [Fact]
        public async Task ScanAsync_AddsRuneterraFromLorFolderAndMatchesStartMenuShortcut()
        {
            string tempRoot = CreateTempDirectory();

            try
            {
                string riotRoot = Path.Combine(tempRoot, "E", "Riot Games");
                string lorRoot = Path.Combine(riotRoot, "LoR");
                Directory.CreateDirectory(Path.Combine(lorRoot, "live", "Game"));
                File.WriteAllText(Path.Combine(lorRoot, "live", "Game", "LoR.exe"), "stub");

                string startMenuPath = Path.Combine(tempRoot, "Start Menu", "Riot Games");
                Directory.CreateDirectory(startMenuPath);
                string shortcutPath = Path.Combine(startMenuPath, "Legends of Runeterra.lnk");
                File.WriteAllText(shortcutPath, "shortcut");

                var scanner = new RiotGamesScanner(
                    () => new[] { riotRoot },
                    startMenuPath);

                var candidates = await scanner.ScanAsync();
                var candidate = Assert.Single(candidates);

                Assert.Equal("Legends of Runeterra", candidate.Name);
                Assert.Equal(lorRoot, candidate.FolderPath);
                Assert.Equal(shortcutPath, candidate.LaunchScriptPath);
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        [Theory]
        [InlineData(DriveType.Fixed, true)]
        [InlineData(DriveType.Removable, true)]
        [InlineData(DriveType.Network, false)]
        [InlineData(DriveType.CDRom, false)]
        [InlineData(DriveType.Ram, false)]
        [InlineData(DriveType.NoRootDirectory, false)]
        [InlineData(DriveType.Unknown, false)]
        public void IsScannableDriveType_MatchesLocalScannerPolicy(DriveType driveType, bool expected)
        {
            Assert.Equal(expected, LocalDriveDiscovery.IsScannableDriveType(driveType));
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), $"codec-riot-scan-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // best effort
            }
        }
    }
}
