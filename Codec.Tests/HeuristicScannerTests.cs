using Codec.Services.Scanning;
using Codec.Services.Scanning.Scanners;
using System.Collections.Concurrent;
using System.Reflection;
using Xunit;

namespace Codec.Tests
{
    public sealed class HeuristicScannerTests
    {
        [Fact]
        public void ComputeGameLikelihoodScore_CountsNestedRuntimeDllsBeforeSubExePenalty()
        {
            string gameDir = CreateTempDirectory("SimCity 2013");

            try
            {
                File.WriteAllText(Path.Combine(gameDir, "SimCity.dll"), "stub");

                string binDir = Path.Combine(gameDir, "Bin");
                Directory.CreateDirectory(binDir);
                File.WriteAllText(Path.Combine(binDir, "SimCity.exe"), "stub");
                File.WriteAllText(Path.Combine(binDir, "SimCity_x86.exe"), "stub");
                File.WriteAllText(Path.Combine(binDir, "Cleanup.exe"), "stub");
                File.WriteAllText(Path.Combine(binDir, "Touchup.exe"), "stub");

                for (int i = 0; i < 8; i++)
                {
                    File.WriteAllText(Path.Combine(binDir, $"runtime{i}.dll"), "stub");
                }

                int score = ComputeGameLikelihoodScore(gameDir);

                Assert.True(score >= -15, $"Expected nested DLL runtime support to avoid rejection, score={score}.");
            }
            finally
            {
                DeleteDirectoryIfExists(gameDir);
            }
        }

        [Fact]
        public void ShouldScanOneLevelDeeper_DescendsIntoFoldersWithoutTopLevelExe()
        {
            string gameDir = CreateTempDirectory("Publisher Folder");

            try
            {
                bool shouldDescend = ShouldScanOneLevelDeeper("Games", gameDir, isContainer: false);

                Assert.True(shouldDescend);
            }
            finally
            {
                DeleteDirectoryIfExists(gameDir);
            }
        }

        [Fact]
        public void TryAddCandidate_RejectsGenericDllOnlySoftwareEvenWhenExeMatchesFolder()
        {
            string softwareDir = CreateTempDirectory("Photo Manager");

            try
            {
                File.WriteAllText(Path.Combine(softwareDir, "PhotoManager.exe"), "stub");
                for (int i = 0; i < 12; i++)
                {
                    File.WriteAllText(Path.Combine(softwareDir, $"runtime{i}.dll"), "stub");
                }

                var candidates = TryAddCandidate(softwareDir);

                Assert.Empty(candidates);
            }
            finally
            {
                DeleteDirectoryIfExists(softwareDir);
            }
        }

        [Fact]
        public void TryAddCandidate_KeepsNestedExecutableGameUnderGamesContainer()
        {
            string gameDir = CreateTempDirectory(Path.Combine("Games", "SimCity 2013"));

            try
            {
                File.WriteAllText(Path.Combine(gameDir, "SimCity.dll"), "stub");

                string binDir = Path.Combine(gameDir, "Bin");
                Directory.CreateDirectory(binDir);
                File.WriteAllText(Path.Combine(binDir, "SimCity.exe"), "stub");
                File.WriteAllText(Path.Combine(binDir, "SimCity_x86.exe"), "stub");
                File.WriteAllText(Path.Combine(binDir, "Cleanup.exe"), "stub");
                File.WriteAllText(Path.Combine(binDir, "Touchup.exe"), "stub");

                for (int i = 0; i < 8; i++)
                {
                    File.WriteAllText(Path.Combine(binDir, $"runtime{i}.dll"), "stub");
                }

                var candidates = TryAddCandidate(gameDir);

                Assert.Single(candidates);
                Assert.Equal("SimCity 2013", candidates.Single().Name);
            }
            finally
            {
                DeleteDirectoryIfExists(gameDir);
            }
        }

        [Fact]
        public void TryAddCandidate_CleansTrailingDomainTagButKeepsFolderPath()
        {
            string gameDir = CreateTempDirectory(Path.Combine("Games", "CloverPit-SteamGG.NET"));

            try
            {
                File.WriteAllText(Path.Combine(gameDir, "steam_appid.txt"), "3299120");
                File.WriteAllText(Path.Combine(gameDir, "CloverPit.exe"), "stub");

                var candidates = TryAddCandidate(gameDir);
                var candidate = Assert.Single(candidates);

                Assert.Equal("CloverPit", candidate.Name);
                Assert.Equal(gameDir, candidate.FolderPath);
            }
            finally
            {
                DeleteDirectoryIfExists(gameDir);
            }
        }

        [Fact]
        public void ExecuteDetectionFunnel_IgnoresInnoSetupUninstaller()
        {
            string gameDir = CreateTempDirectory(Path.Combine("Games", "LoveChoice-Steamrip.com"));

            try
            {
                File.WriteAllText(Path.Combine(gameDir, "LoveChoice.exe"), "game");
                File.WriteAllText(Path.Combine(gameDir, "unins000.exe"), new string('x', 1024));

                string selected = ExecutableDetector.ExecuteDetectionFunnel(gameDir, "LoveChoice");

                Assert.Equal("LoveChoice.exe", Path.GetFileName(selected));
            }
            finally
            {
                DeleteDirectoryIfExists(gameDir);
            }
        }

        [Fact]
        public void TryAddCandidate_RejectsEpicLauncherEngineWhenLauncherIsExcluded()
        {
            string root = Path.Combine(Path.GetTempPath(), $"codec-epic-{Guid.NewGuid():N}");
            string launcherDir = Path.Combine(root, "Epic Games", "Launcher");
            string engineDir = Path.Combine(launcherDir, "Engine");
            string binDir = Path.Combine(engineDir, "Binaries", "Win64");
            Directory.CreateDirectory(binDir);

            try
            {
                File.WriteAllText(Path.Combine(binDir, "EpicGamesLauncher.exe"), "stub");
                for (int i = 0; i < 12; i++)
                {
                    File.WriteAllText(Path.Combine(binDir, $"runtime{i}.dll"), "stub");
                }

                var scanner = new HeuristicScanner();
                scanner.SetExcludedPaths(new[] { launcherDir });

                var candidates = new ConcurrentBag<GameCandidate>();
                var method = typeof(HeuristicScanner).GetMethod(
                    "TryAddCandidate",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);
                method.Invoke(scanner, new object[] { engineDir, "Engine", candidates });

                Assert.Empty(candidates);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        private static int ComputeGameLikelihoodScore(string directory)
        {
            var method = typeof(HeuristicScanner).GetMethod(
                "ComputeGameLikelihoodScore",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);
            return (int)method.Invoke(null, new object[] { directory })!;
        }

        private static bool ShouldScanOneLevelDeeper(string rootName, string directory, bool isContainer)
        {
            var method = typeof(HeuristicScanner).GetMethod(
                "ShouldScanOneLevelDeeper",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);
            return (bool)method.Invoke(null, new object[] { rootName, directory, isContainer })!;
        }

        private static IReadOnlyCollection<GameCandidate> TryAddCandidate(string directory)
        {
            var method = typeof(HeuristicScanner).GetMethod(
                "TryAddCandidate",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);
            var scanner = new HeuristicScanner();
            var candidates = new ConcurrentBag<GameCandidate>();
            string dirName = new DirectoryInfo(directory).Name;
            method.Invoke(scanner, new object[] { directory, dirName, candidates });
            return candidates.ToArray();
        }

        private static string CreateTempDirectory(string directoryName)
        {
            string root = Path.Combine(Path.GetTempPath(), $"codec-heuristic-{Guid.NewGuid():N}");
            string path = Path.Combine(root, directoryName);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            try
            {
                string? root = Directory.GetParent(path)?.FullName;
                if (root is not null && Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
