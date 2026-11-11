using Codec.Models;
using Xunit;

namespace Codec.Tests;

public sealed class GameMergeTests
{
    [Fact]
    public void MergeFetchedMetadataFrom_FullEnrichmentReplacesFetchedFields()
    {
        var existing = new Game { Name = "Placeholder", SteamID = 620, IsInstalled = true };
        Game enriched = CreateEnrichedGame();

        existing.MergeFetchedMetadataFrom(enriched);

        Assert.Equal("Portal 2", existing.Name);
        Assert.Equal(1234, existing.IgdbId);
        Assert.Equal(55, existing.GridDbId);
        Assert.Equal("A puzzle game.", existing.Description);
        Assert.Equal(["Puzzle"], existing.Genres);
        Assert.Equal(["Science fiction"], existing.Themes);
        Assert.Equal(["Single player"], existing.GameModes);
        Assert.Equal(["https://example.test/shot.jpg"], existing.Media);
        Assert.Equal("https://example.test/logo.png", existing.LibraryLogoUrl);
        Assert.Equal(@"C:\cache\logo.png", existing.LibraryLogoCache);
        Assert.True(existing.HasLogoAssetSource);
        Assert.Equal(30600, existing.TimeToCompleteMainStory);
        Assert.Equal("Portal", existing.FranchiseName);
        Assert.Equal("https://igdb.example/portal-2", existing.IgdbUrl);
        Assert.Equal(ControllerSupportLevel.Full, existing.ControllerSupport);
        Assert.True(existing.IsControllerRecommended);
        Assert.True(existing.IsFullyImported);
    }

    [Fact]
    public void MergeFetchedMetadataFrom_EmptyEnrichmentChangesNothing()
    {
        Game existing = CreateEnrichedGame();
        Game before = existing.CreateHydrationSnapshot();
        // The import's own defaults: a fresh game is installed, and nothing was fetched.
        var empty = new Game { IsInstalled = existing.IsInstalled, IsSteamOwned = existing.IsSteamOwned };

        existing.MergeFetchedMetadataFrom(empty);

        Assert.Equivalent(before, existing.CreateHydrationSnapshot(), strict: true);
    }

    [Fact]
    public void MergeFetchedMetadataFrom_LeavesUserOwnedStateAlone()
    {
        var addedOn = new DateTime(2024, 3, 1);
        var existing = new Game
        {
            Name = "Portal 2",
            SteamID = 620,
            DateAdded = addedOn,
            IsFavorite = true,
            LaunchScript = @"C:\scripts\portal.bat",
            UseLaunchScriptOverride = true,
            UseExecutableOverride = true,
            Executable = @"D:\custom\portal2.exe"
        };
        Guid id = existing.Id;

        // A copy taken before the player changed anything, as the Steam sync hands back.
        Game enriched = CreateEnrichedGame();
        enriched.Id = Guid.NewGuid();
        enriched.DateAdded = DateTime.Now;
        enriched.IsFavorite = false;
        enriched.LaunchScript = null;
        enriched.UseLaunchScriptOverride = false;
        enriched.UseExecutableOverride = false;
        enriched.Executable = @"C:\Steam\steamapps\common\Portal 2\portal2.exe";

        existing.MergeFetchedMetadataFrom(enriched);

        Assert.Equal(id, existing.Id);
        Assert.Equal(addedOn, existing.DateAdded);
        Assert.True(existing.IsFavorite);
        Assert.Equal(@"C:\scripts\portal.bat", existing.LaunchScript);
        Assert.True(existing.UseLaunchScriptOverride);
        Assert.True(existing.UseExecutableOverride);
        Assert.Equal(@"D:\custom\portal2.exe", existing.Executable);
    }

    [Fact]
    public void MergeFetchedMetadataFrom_InstalledStateChangeApplies()
    {
        Game existing = CreateEnrichedGame();
        existing.IsInstalled = true;
        var uninstalled = new Game { IsInstalled = false, IsSteamOwned = true };

        existing.MergeFetchedMetadataFrom(uninstalled);

        Assert.False(existing.IsInstalled);
        Assert.Equal(1234, existing.IgdbId);
    }

    [Fact]
    public void MergeFetchedMetadataFrom_KeepsTheNewerActivityAndAchievementCheck()
    {
        var played = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var checkedUtc = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc);
        var existing = new Game
        {
            LastPlayedUtc = played,
            SteamAchievementsLastCheckedUtc = checkedUtc,
            SteamAchievementsUnlocked = 40,
            SteamAchievementsTotal = 51
        };
        var stale = new Game
        {
            LastPlayedUtc = played.AddDays(-10),
            SteamAchievementsLastCheckedUtc = checkedUtc.AddDays(-10),
            SteamAchievementsUnlocked = 3,
            SteamAchievementsTotal = 51
        };

        existing.MergeFetchedMetadataFrom(stale);

        Assert.Equal(played, existing.LastPlayedUtc);
        Assert.Equal(checkedUtc, existing.SteamAchievementsLastCheckedUtc);
        Assert.Equal(40, existing.SteamAchievementsUnlocked);

        var newer = new Game
        {
            LastPlayedUtc = played.AddDays(1),
            SteamAchievementsLastCheckedUtc = checkedUtc.AddDays(1),
            SteamAchievementsUnlocked = 45,
            SteamAchievementsTotal = 51
        };

        existing.MergeFetchedMetadataFrom(newer);

        Assert.Equal(played.AddDays(1), existing.LastPlayedUtc);
        Assert.Equal(checkedUtc.AddDays(1), existing.SteamAchievementsLastCheckedUtc);
        Assert.Equal(45, existing.SteamAchievementsUnlocked);
    }

    [Fact]
    public void MergeFetchedMetadataFrom_EmptyTaxonomyCountsAsChecked()
    {
        var existing = new Game { Name = "Obscure" };
        var enriched = new Game { Genres = [], Themes = [], GameModes = [] };

        existing.MergeFetchedMetadataFrom(enriched);

        Assert.True(existing.IsIgdbTaxonomyChecked);
    }

    private static Game CreateEnrichedGame() => new()
    {
        Name = "Portal 2",
        SteamID = 620,
        ImportedFrom = "Steam",
        FolderLocation = @"C:\Steam\steamapps\common\Portal 2",
        IsInstalled = true,
        IsSteamOwned = true,
        IgdbId = 1234,
        GridDbId = 55,
        Description = "A puzzle game.",
        Publisher = "Valve",
        Developer = "Valve",
        Genres = ["Puzzle"],
        Themes = ["Science fiction"],
        GameModes = ["Single player"],
        Platforms = ["PC (Microsoft Windows)"],
        Media = ["https://example.test/shot.jpg"],
        ReleaseDate = new DateTime(2011, 4, 19),
        FranchiseName = "Portal",
        IgdbFranchiseId = 7,
        TimeToCompleteMainStory = 30600,
        TimeToCompleteCompletionist = 72000,
        ControllerSupport = ControllerSupportLevel.Full,
        IsControllerRecommended = true,
        HasHeroAssetSource = true,
        HasLogoAssetSource = true,
        LibraryCapsuleUrl = "https://example.test/capsule.jpg",
        LibraryCapsuleCache = @"C:\cache\capsule.jpg",
        LibraryHeroUrl = "https://example.test/hero.jpg",
        LibraryHeroCache = @"C:\cache\hero.jpg",
        LibraryLogoUrl = "https://example.test/logo.png",
        LibraryLogoCache = @"C:\cache\logo.png",
        SteamPageUrl = "https://store.steampowered.com/app/620",
        IgdbUrl = "https://igdb.example/portal-2",
        HltbUrl = "https://hltb.example/portal-2",
        IsFullyImported = true
    };
}
