using Codec.Services.Scanning;
using Codec.Services.Scanning.Scanners;
using Codec.Services.Importing;
using Codec.ViewModels;
using Xunit;

namespace Codec.Tests
{
    public sealed class GameScannerTests
    {
        [Fact]
        public void ShouldUseFullExecutableDetection_OnlyForHeuristicNonSteamCandidates()
        {
            Assert.True(GameScanner.ShouldUseFullExecutableDetection(
                new GameCandidate("Haste", @"E:\Games\Haste", "Heuristic Scan")));

            Assert.False(GameScanner.ShouldUseFullExecutableDetection(
                new GameCandidate("Legends of Runeterra", @"E:\Riot Games\LoR", "Riot Games", LaunchScriptPath: @"C:\LoR.lnk")));

            Assert.False(GameScanner.ShouldUseFullExecutableDetection(
                new GameCandidate("Epic Game", @"E:\Epic\Epic Game", "Epic Games", EpicAppId: "EpicGame")));

            Assert.False(GameScanner.ShouldUseFullExecutableDetection(
                new GameCandidate("Steam Game", @"E:\Steam\steamapps\common\Steam Game", "Steam", SteamAppId: 123)));
        }

        [Fact]
        public void CanTrustMissingExecutable_ForLauncherCandidatesWithLaunchTargets()
        {
            Assert.True(GameScanner.CanTrustMissingExecutable(
                new GameCandidate("Legends of Runeterra", @"E:\Riot Games\LoR", "Riot Games")));
            Assert.True(GameScanner.CanTrustMissingExecutable(
                new GameCandidate("Fortnite", @"E:\Epic Games\Fortnite", "Epic Games", EpicAppId: "FortniteGame")));
            Assert.False(GameScanner.CanTrustMissingExecutable(
                new GameCandidate("Epic Game", @"E:\Epic Games\Epic Game", "Epic Games")));
            Assert.False(GameScanner.CanTrustMissingExecutable(
                new GameCandidate("Haste", @"E:\Games\Haste", "Heuristic Scan")));
        }

        [Fact]
        public void ImportPipeline_AllowsMissingExecutableForLauncherTargets()
        {
            var riotRequest = new GameImportRequest(
                ExecutablePath: string.Empty,
                FolderLocation: @"E:\Riot Games\VALORANT",
                NameHint: "VALORANT",
                ImportSource: "Riot Games");

            var heuristicRequest = riotRequest with
            {
                ImportSource = "Heuristic Scan"
            };
            var epicRequest = riotRequest with
            {
                FolderLocation = @"E:\Epic Games\Fortnite",
                NameHint = "Fortnite",
                ImportSource = "Epic Games",
                EpicAppId = "FortniteGame"
            };
            var epicSourceOnlyRequest = epicRequest with
            {
                EpicAppId = null
            };

            Assert.True(GameImportPipeline.AllowsMissingExecutable(riotRequest, hasLaunchScript: false));
            Assert.True(GameImportPipeline.AllowsMissingExecutable(epicRequest, hasLaunchScript: false));
            Assert.False(GameImportPipeline.AllowsMissingExecutable(epicSourceOnlyRequest, hasLaunchScript: false));
            Assert.False(GameImportPipeline.AllowsMissingExecutable(heuristicRequest, hasLaunchScript: false));
        }

        [Fact]
        public void BuildEpicLaunchUri_UsesEpicProtocolWithEscapedAppId()
        {
            Assert.Equal(
                "com.epicgames.launcher://apps/FortniteGame?action=launch&silent=true",
                MainViewModel.BuildEpicLaunchUri("FortniteGame"));
        }
    }
}
