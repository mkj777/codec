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

        [Theory]
        [InlineData("It Takes Two Friend's Pass", "It Takes Two")]
        [InlineData("It Takes Two Friend\u2019s Pass", "It Takes Two")]
        [InlineData("It Takes Two - Friend's Pass", "It Takes Two")]
        [InlineData("IT TAKES TWO FRIEND'S PASS", "IT TAKES TWO")]
        public void TryGetFriendPassBaseName_StripsTrailingSuffix(string input, string expected)
        {
            bool stripped = GameNameCleaner.TryGetFriendPassBaseName(input, out string baseName);

            Assert.True(stripped);
            Assert.Equal(expected, baseName);
        }

        [Theory]
        [InlineData("Friend's Pass")]
        [InlineData("Friend's Pass Collection")]
        [InlineData("Pass the Friends")]
        [InlineData("It Takes Two")]
        public void TryGetFriendPassBaseName_DoesNotStripNonSuffixNames(string input)
        {
            bool stripped = GameNameCleaner.TryGetFriendPassBaseName(input, out string baseName);

            Assert.False(stripped);
            Assert.Equal(string.Empty, baseName);
        }
    }
}
