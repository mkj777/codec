using Codec.Models;
using Codec.Services.Steam;
using Codec.ViewModels;
using Xunit;

namespace Codec.Tests;

public sealed class SteamIntegrationTests
{
    [Fact]
    public void OwnedOnlySteamApp_CanInstallButCannotLaunch()
    {
        var game = new Game
        {
            ImportedFrom = "Steam",
            SteamID = 620,
            IsSteamOwned = true,
            IsInstalled = false
        };

        Assert.True(game.IsOwnedOnly);
        Assert.True(game.CanInstall);
        Assert.False(game.CanLaunch);
        Assert.Equal(0.68d, game.LibraryCardOpacity);
    }

    [Fact]
    public void InstalledSteamApp_CanLaunchButDoesNotOfferInstall()
    {
        var game = new Game
        {
            ImportedFrom = "Steam",
            SteamID = 620,
            IsSteamOwned = true,
            IsInstalled = true
        };

        Assert.True(game.CanLaunch);
        Assert.False(game.CanInstall);
        Assert.Equal(1d, game.LibraryCardOpacity);
    }

    [Fact]
    public void RemoveOwnedOnlyGames_PreservesInstalledAndNonSteamEntries()
    {
        var ownedOnly = new Game { IsSteamOwned = true, IsInstalled = false, SteamID = 10 };
        var installed = new Game { IsSteamOwned = true, IsInstalled = true, SteamID = 20 };
        var local = new Game { IsSteamOwned = false, IsInstalled = false };
        var library = new List<Game> { ownedOnly, installed, local };

        int removed = SteamLibraryService.RemoveOwnedOnlyGames(library);

        Assert.Equal(1, removed);
        Assert.DoesNotContain(ownedOnly, library);
        Assert.Contains(installed, library);
        Assert.Contains(local, library);
    }

    [Fact]
    public void BuildSteamInstallUri_UsesSteamProtocol()
        => Assert.Equal("steam://install/730", MainViewModel.BuildSteamInstallUri(730));

    [Theory]
    [InlineData("game", true)]
    [InlineData("Game", true)]
    [InlineData("tool", false)]
    [InlineData("application", false)]
    [InlineData("software", false)]
    [InlineData("dlc", false)]
    public void SteamSync_OnlyAcceptsGameAppType(string appType, bool expected)
        => Assert.Equal(expected, SteamAuthService.IsGameAppType(appType));
}
