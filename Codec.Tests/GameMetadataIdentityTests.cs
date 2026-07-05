using Codec.Models;
using Xunit;

namespace Codec.Tests
{
    public sealed class GameMetadataIdentityTests
    {
        [Fact]
        public void FriendPass_UsesBaseNameForMetadataWithoutFallingBackToPassSteamId()
        {
            var game = new Game
            {
                Name = "It Takes Two Friend's Pass",
                ImportedFrom = "Steam",
                SteamID = 111111
            };

            Assert.Equal("It Takes Two Friend's Pass", game.Name);
            Assert.Equal("It Takes Two", game.EffectiveMetadataLookupName);
            Assert.True(game.UsesAlternateMetadataLookupName);
            Assert.Null(game.EffectiveSteamMetadataAppId);
            Assert.True(game.IsSteamLaunchTarget);
        }

        [Fact]
        public void FriendPass_UsesSeparateSteamMetadataIdWhenResolved()
        {
            var game = new Game
            {
                Name = "It Takes Two Friend's Pass",
                ImportedFrom = "Steam",
                SteamID = 111111,
                SteamMetadataAppId = 1426210,
                MetadataLookupName = "It Takes Two"
            };

            Assert.Equal(111111, game.SteamID);
            Assert.Equal(1426210, game.EffectiveSteamMetadataAppId);
            Assert.Equal("It Takes Two", game.EffectiveMetadataLookupName);
        }

        [Fact]
        public void NormalSteamGame_UsesLaunchSteamIdAsMetadataId()
        {
            var game = new Game
            {
                Name = "Portal 2",
                ImportedFrom = "Steam",
                SteamID = 620
            };

            Assert.Equal(620, game.EffectiveSteamMetadataAppId);
            Assert.Equal("Portal 2", game.EffectiveMetadataLookupName);
            Assert.False(game.UsesAlternateMetadataLookupName);
        }

        [Fact]
        public void HydrationSnapshot_PreservesMetadataIdentity()
        {
            var game = new Game
            {
                Name = "It Takes Two Friend's Pass",
                ImportedFrom = "Steam",
                SteamID = 111111,
                SteamMetadataAppId = 1426210,
                MetadataLookupName = "It Takes Two"
            };

            var snapshot = game.CreateHydrationSnapshot();
            var target = new Game();
            target.ApplyHydrationSnapshot(snapshot);

            Assert.Equal(111111, target.SteamID);
            Assert.Equal(1426210, target.SteamMetadataAppId);
            Assert.Equal(1426210, target.EffectiveSteamMetadataAppId);
            Assert.Equal("It Takes Two", target.MetadataLookupName);
            Assert.Equal("It Takes Two", target.EffectiveMetadataLookupName);
        }
    }
}
