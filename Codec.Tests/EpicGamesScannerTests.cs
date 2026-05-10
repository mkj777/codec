using Codec.Services.Scanning.Scanners;
using System.Text.Json;
using Xunit;

namespace Codec.Tests
{
    public sealed class EpicGamesScannerTests
    {
        [Fact]
        public async Task ScanAsync_AddsApplicationManifestWithEpicAppIdAndExecutableHint()
        {
            string tempRoot = CreateTempDirectory();

            try
            {
                string manifestsPath = Path.Combine(tempRoot, "Manifests");
                string installPath = Path.Combine(tempRoot, "Epic Games", "Fortnite");
                Directory.CreateDirectory(manifestsPath);
                Directory.CreateDirectory(Path.Combine(installPath, "FortniteGame", "Binaries", "Win64"));
                string executablePath = Path.Combine(installPath, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe");
                File.WriteAllText(executablePath, "stub");

                WriteManifest(
                    Path.Combine(manifestsPath, "fortnite.item"),
                    displayName: "Fortnite",
                    installLocation: installPath,
                    appName: "FortniteGame",
                    isApplication: true,
                    launchExecutable: @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe");

                var scanner = new EpicGamesScanner(manifestsPath);
                var candidates = await scanner.ScanAsync();
                var candidate = Assert.Single(candidates);

                Assert.Equal("Fortnite", candidate.Name);
                Assert.Equal(Path.GetFullPath(installPath), candidate.FolderPath);
                Assert.Equal("Epic Games Store", candidate.Source);
                Assert.Equal("FortniteGame", candidate.EpicAppId);
                Assert.Equal(Path.GetFullPath(executablePath), candidate.ExecutableHintPath);
                Assert.Contains(Path.GetFullPath(installPath), scanner.KnownLibraryPaths);
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        [Fact]
        public async Task ScanAsync_SkipsNonApplicationManifests()
        {
            string tempRoot = CreateTempDirectory();

            try
            {
                string manifestsPath = Path.Combine(tempRoot, "Manifests");
                string installPath = Path.Combine(tempRoot, "Epic Games", "UnrealEngine");
                Directory.CreateDirectory(manifestsPath);
                Directory.CreateDirectory(installPath);
                File.WriteAllText(Path.Combine(installPath, "Engine.exe"), "stub");

                WriteManifest(
                    Path.Combine(manifestsPath, "engine.item"),
                    displayName: "Unreal Engine",
                    installLocation: installPath,
                    appName: "UE_5.4",
                    isApplication: false);

                var scanner = new EpicGamesScanner(manifestsPath);
                var candidates = await scanner.ScanAsync();

                Assert.Empty(candidates);
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        [Fact]
        public async Task ScanAsync_SkipsEmptyAndMissingInstallFolders()
        {
            string tempRoot = CreateTempDirectory();

            try
            {
                string manifestsPath = Path.Combine(tempRoot, "Manifests");
                string emptyInstallPath = Path.Combine(tempRoot, "Epic Games", "Empty");
                string missingInstallPath = Path.Combine(tempRoot, "Epic Games", "Missing");
                Directory.CreateDirectory(manifestsPath);
                Directory.CreateDirectory(emptyInstallPath);

                WriteManifest(
                    Path.Combine(manifestsPath, "empty.item"),
                    displayName: "Empty Game",
                    installLocation: emptyInstallPath,
                    appName: "EmptyGame",
                    isApplication: true);
                WriteManifest(
                    Path.Combine(manifestsPath, "missing.item"),
                    displayName: "Missing Game",
                    installLocation: missingInstallPath,
                    appName: "MissingGame",
                    isApplication: true);

                var scanner = new EpicGamesScanner(manifestsPath);
                var candidates = await scanner.ScanAsync();

                Assert.Empty(candidates);
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        [Fact]
        public async Task ScanAsync_IgnoresMalformedManifests()
        {
            string tempRoot = CreateTempDirectory();

            try
            {
                string manifestsPath = Path.Combine(tempRoot, "Manifests");
                Directory.CreateDirectory(manifestsPath);
                File.WriteAllText(Path.Combine(manifestsPath, "broken.item"), "{ nope");

                var scanner = new EpicGamesScanner(manifestsPath);
                var candidates = await scanner.ScanAsync();

                Assert.Empty(candidates);
            }
            finally
            {
                DeleteDirectoryIfExists(tempRoot);
            }
        }

        private static void WriteManifest(
            string path,
            string displayName,
            string installLocation,
            string appName,
            bool isApplication,
            string? launchExecutable = null)
        {
            var payload = new Dictionary<string, object?>
            {
                ["DisplayName"] = displayName,
                ["InstallLocation"] = installLocation,
                ["AppName"] = appName,
                ["LaunchExecutable"] = launchExecutable,
                ["bIsApplication"] = isApplication,
                ["bIsManaged"] = true,
                ["CatalogNamespace"] = "fn",
                ["CatalogItemId"] = "catalog-item"
            };

            File.WriteAllText(path, JsonSerializer.Serialize(payload));
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), $"codec-epic-scan-{Guid.NewGuid():N}");
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
