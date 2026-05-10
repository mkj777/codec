using Codec.Services;
using Xunit;

namespace Codec.Tests
{
    public sealed class GameNameCleanerTests
    {
        [Theory]
        [InlineData("CloverPit-SteamGG.NET", "CloverPit")]
        [InlineData("Fracture Field - SteamGG.NET", "Fracture Field")]
        [InlineData("Chop Chains.SteamGG.NET", "Chop Chains")]
        [InlineData("No-I-not-a-Human-SteamRIP.com", "No-I-not-a-Human")]
        [InlineData("LoveChoice-Steamrip.com", "LoveChoice")]
        [InlineData("Game Steamrip.com", "Game")]
        [InlineData("Game.anything.ru", "Game")]
        [InlineData("Game - MIXEDCase.CoM", "Game")]
        [InlineData("Game-SteamGG.NET-SteamRIP.com", "Game")]
        public void RemoveTrailingDomainTag_StripsFinalDomainTag(string input, string expected)
        {
            Assert.Equal(expected, GameNameCleaner.RemoveTrailingDomainTag(input));
        }

        [Theory]
        [InlineData("SteamWorld Dig")]
        [InlineData("Game-SteamGG.NET-Deluxe")]
        [InlineData("Dot Net Racing")]
        public void RemoveTrailingDomainTag_OnlyStripsTrailingDomainTags(string input)
        {
            Assert.Equal(input, GameNameCleaner.RemoveTrailingDomainTag(input));
        }

        [Fact]
        public void RemoveTrailingDomainTag_IsStableAfterCleanup()
        {
            string cleaned = GameNameCleaner.RemoveTrailingDomainTag("CloverPit-SteamGG.NET");

            Assert.Equal(cleaned, GameNameCleaner.RemoveTrailingDomainTag(cleaned));
        }
    }
}
