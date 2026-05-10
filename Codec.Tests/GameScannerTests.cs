using Codec.Services.Scanning;
using Codec.Services.Scanning.Scanners;
using Codec.Services.Importing;
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
                new GameCandidate("Epic Game", @"E:\Epic\Epic Game", "Epic Games Store")));

            Assert.False(GameScanner.ShouldUseFullExecutableDetection(
                new GameCandidate("Steam Game", @"E:\Steam\steamapps\common\Steam Game", "Steam", SteamAppId: 123)));
        }

        [Fact]
        public void CanTrustMissingExecutable_ForRiotGamesScannerCandidates()
        {
            Assert.True(GameScanner.CanTrustMissingExecutable("Riot Games"));
            Assert.False(GameScanner.CanTrustMissingExecutable("Epic Games Store"));
            Assert.False(GameScanner.CanTrustMissingExecutable("Heuristic Scan"));
        }

        [Fact]
        public void ImportPipeline_AllowsMissingExecutableForRiotGames()
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

            Assert.True(GameImportPipeline.AllowsMissingExecutable(riotRequest, hasLaunchScript: false));
            Assert.False(GameImportPipeline.AllowsMissingExecutable(heuristicRequest, hasLaunchScript: false));
        }
    }
}
