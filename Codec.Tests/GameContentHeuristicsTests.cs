using Codec.Services.Scanning;
using Xunit;

namespace Codec.Tests
{
    public sealed class GameContentHeuristicsTests
    {
        [Fact]
        public void ShouldIgnoreCandidate_DoesNotTreatMortuaryAssistantAsUtility()
        {
            bool ignore = GameContentHeuristics.ShouldIgnoreCandidate(
                "The Mortuary Assistant",
                @"E:\Games\The Mortuary Assistant",
                "Heuristic Scan",
                hasSteamAppId: false);

            Assert.False(ignore);
        }

        [Fact]
        public void ShouldIgnoreCandidate_StillRejectsObviousSupportUtilities()
        {
            bool ignore = GameContentHeuristics.ShouldIgnoreCandidate(
                "Driver Support Tool",
                @"C:\Program Files\Driver Support Tool",
                "Heuristic Scan",
                hasSteamAppId: false);

            Assert.True(ignore);
        }
    }
}
