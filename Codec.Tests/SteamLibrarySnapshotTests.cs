using Codec.Models;
using Codec.Services.Scanning.Scanners;
using Codec.Services.Steam;
using System;
using System.Collections.Generic;
using Xunit;

namespace Codec.Tests;

/// <summary>
/// The Steam sync runs on the thread pool. These pin that it never writes to the games it is
/// handed (on the UI those are bound), and that what it would have written comes back as
/// updates the UI thread applies instead.
/// </summary>
public sealed class SteamLibrarySnapshotTests
{
    private static readonly IReadOnlyDictionary<int, GameCandidate> NothingInstalled =
        new Dictionary<int, GameCandidate>();

    [Fact]
    public void Reconcile_NeverWritesToTheGamesItIsHanded()
    {
        var local = new Game
        {
            Name = "Portal 2",
            SteamID = 620,
            ImportedFrom = "Steam",
            IsInstalled = false,
            FolderLocation = @"D:\Old\Portal 2"
        };
        var library = new List<Game> { local };
        var changes = new List<string?>();
        local.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var installed = new Dictionary<int, GameCandidate>
        {
            [620] = new GameCandidate("Portal 2", @"E:\Steam\Portal 2", "Steam", SteamAppId: 620, ExecutableHintPath: @"E:\Steam\Portal 2\portal2.exe")
        };

        SteamOwnedReconciliation result = SteamLibraryService.ReconcileOwnedApps(
            library,
            [new SteamOwnedApp(620, "Portal 2", "game"), new SteamOwnedApp(400, "Portal", "game")],
            installed,
            _ => false,
            TestContext.Current.CancellationToken);

        Assert.Empty(changes);
        Assert.False(local.IsSteamOwned);
        Assert.Equal(@"D:\Old\Portal 2", local.FolderLocation);
        Assert.All(result.Games, item => Assert.NotSame(local, item.Game));
        Assert.Single(library);
    }

    [Fact]
    public void Reconcile_ReturnsTheExistingGameChangeAsAnUpdate()
    {
        var local = new Game { Name = "Portal 2", SteamID = 620, ImportedFrom = "Steam", IsInstalled = false };
        var installed = new Dictionary<int, GameCandidate>
        {
            [620] = new GameCandidate("Portal 2", @"E:\Steam\Portal 2", "Steam", SteamAppId: 620, ExecutableHintPath: @"E:\Steam\Portal 2\portal2.exe")
        };

        SteamOwnedReconciliation result = SteamLibraryService.ReconcileOwnedApps(
            [local],
            [new SteamOwnedApp(620, "Portal 2", "game")],
            installed,
            _ => false,
            TestContext.Current.CancellationToken);

        SteamOwnershipUpdate update = Assert.Single(result.OwnershipUpdates);
        Assert.Equal(local.Id, update.GameId);
        Assert.True(update.IsInstalled);
        Assert.Equal(@"E:\Steam\Portal 2", update.FolderLocation);
        Assert.Equal(@"E:\Steam\Portal 2\portal2.exe", update.Executable);
        Assert.Equal(1, result.UpdatedCount);

        (Game working, bool isNew) = Assert.Single(result.Games);
        Assert.False(isNew);
        Assert.Equal(local.Id, working.Id);
        Assert.True(working.IsSteamOwned);
        Assert.Equal(@"E:\Steam\Portal 2", working.FolderLocation);

        update.ApplyTo(local);
        Assert.True(local.IsSteamOwned);
        Assert.True(local.IsInstalled);
        Assert.Equal("game", local.SteamAppType);
        Assert.Equal(@"E:\Steam\Portal 2", local.FolderLocation);
        Assert.Equal(@"E:\Steam\Portal 2\portal2.exe", local.Executable);
    }

    [Fact]
    public void Reconcile_KeepsANonSteamInstallWhereItIs()
    {
        var local = new Game
        {
            Name = "Celeste",
            SteamID = 504230,
            ImportedFrom = "Heuristic Scan",
            FolderLocation = @"D:\Games\Celeste",
            Executable = @"D:\Games\Celeste\Celeste.exe"
        };
        var installed = new Dictionary<int, GameCandidate>
        {
            [504230] = new GameCandidate("Celeste", @"E:\Steam\Celeste", "Steam", SteamAppId: 504230)
        };

        SteamOwnedReconciliation result = SteamLibraryService.ReconcileOwnedApps(
            [local],
            [new SteamOwnedApp(504230, "Celeste", "game")],
            installed,
            folder => folder == @"D:\Games\Celeste",
            TestContext.Current.CancellationToken);

        SteamOwnershipUpdate update = Assert.Single(result.OwnershipUpdates);
        Assert.Null(update.FolderLocation);
        Assert.Null(update.Executable);

        update.ApplyTo(local);
        Assert.Equal(@"D:\Games\Celeste", local.FolderLocation);
        Assert.Equal(@"D:\Games\Celeste\Celeste.exe", local.Executable);
    }

    [Fact]
    public void Reconcile_SkipsAnUpdateWhenNothingChanges_ButStillCountsTheGame()
    {
        var local = new Game
        {
            Name = "Portal",
            SteamID = 400,
            ImportedFrom = "Steam",
            IsSteamOwned = true,
            IsInstalled = false,
            SteamAppType = "game"
        };

        SteamOwnedReconciliation result = SteamLibraryService.ReconcileOwnedApps(
            [local],
            [new SteamOwnedApp(400, "Portal", "game")],
            NothingInstalled,
            _ => false,
            TestContext.Current.CancellationToken);

        Assert.Empty(result.OwnershipUpdates);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Single(result.Games);
    }

    [Fact]
    public void Reconcile_MatchesTheFirstGameWithTheAppId_AsTheLiveListLookupDid()
    {
        var first = new Game { Name = "First", SteamID = 400, ImportedFrom = "Steam" };
        var second = new Game { Name = "Second", SteamID = 400, ImportedFrom = "Steam" };

        SteamOwnedReconciliation result = SteamLibraryService.ReconcileOwnedApps(
            [first, second],
            [new SteamOwnedApp(400, "Portal", "game")],
            NothingInstalled,
            _ => false,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, Assert.Single(result.OwnershipUpdates).GameId);
    }

    [Fact]
    public void Reconcile_TakesItsOwnCopyOfTheList()
    {
        var library = new List<Game> { new() { Name = "Portal", SteamID = 400, ImportedFrom = "Steam" } };

        SteamOwnedReconciliation result = SteamLibraryService.ReconcileOwnedApps(
            library,
            [new SteamOwnedApp(400, "Portal", "game"), new SteamOwnedApp(620, "Portal 2", "game")],
            NothingInstalled,
            _ => false,
            TestContext.Current.CancellationToken);
        library.Clear();
        library.Add(new Game { Name = "Late", SteamID = 620, ImportedFrom = "Steam" });

        Assert.Equal(2, result.Games.Count);
        Assert.Equal(1, result.UpdatedCount);
        Assert.True(result.Games[1].IsNew);
    }

    [Fact]
    public void Reconcile_BuildsNewOwnedGamesItOwnsOutright()
    {
        SteamOwnedReconciliation result = SteamLibraryService.ReconcileOwnedApps(
            [],
            [new SteamOwnedApp(620, "Portal 2", "game")],
            NothingInstalled,
            _ => false,
            TestContext.Current.CancellationToken);

        (Game game, bool isNew) = Assert.Single(result.Games);
        Assert.True(isNew);
        Assert.Equal(620, game.SteamID);
        Assert.True(game.IsSteamOwned);
        Assert.False(game.IsInstalled);
        Assert.Empty(result.OwnershipUpdates);
        Assert.Equal(0, result.UpdatedCount);
    }

    [Fact]
    public void OwnershipUpdate_KeepsAnExecutableThePlayerChose()
    {
        var local = new Game
        {
            Name = "Skyrim Special Edition",
            SteamID = 489830,
            ImportedFrom = "Steam",
            FolderLocation = @"D:\Old\Skyrim",
            Executable = @"D:\Old\Skyrim\skse64_loader.exe",
            UseExecutableOverride = true
        };
        var update = new SteamOwnershipUpdate(
            local.Id,
            true,
            "game",
            @"E:\Steam\Skyrim",
            @"E:\Steam\Skyrim\SkyrimSE.exe");

        update.ApplyTo(local);

        Assert.Equal(@"D:\Old\Skyrim\skse64_loader.exe", local.Executable);
        Assert.Equal(@"E:\Steam\Skyrim", local.FolderLocation);
        Assert.True(local.IsInstalled);
    }

    [Fact]
    public void OwnershipUpdate_MovesTheExecutableWhenThereIsNoOverride()
    {
        var local = new Game { Name = "Skyrim Special Edition", SteamID = 489830, Executable = @"D:\Old\Skyrim\SkyrimSE.exe" };

        new SteamOwnershipUpdate(local.Id, true, "game", @"E:\Steam\Skyrim", @"E:\Steam\Skyrim\SkyrimSE.exe").ApplyTo(local);

        Assert.Equal(@"E:\Steam\Skyrim\SkyrimSE.exe", local.Executable);
    }

    [Fact]
    public void Enrichment_LeavesHiddenGamesOut()
    {
        var visible = new Game { Name = "Portal 2", SteamID = 620, ImportedFrom = "Steam", IsFullyImported = false };
        var hidden = new Game { Name = "Steamworks Common Redistributables", SteamID = 228980, ImportedFrom = "Steam", IsFullyImported = false, IsHidden = true };

        Assert.True(SteamLibraryService.NeedsEnrichment(visible));
        Assert.False(SteamLibraryService.NeedsEnrichment(hidden));
    }

    [Fact]
    public void AchievementUpdate_OnlyMovesLastPlayedForward()
    {
        var played = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var game = new Game { SteamID = 620, LastPlayedUtc = played };

        new SteamAchievementUpdate(620, 3, 10, played, played.AddDays(-5)).ApplyTo(game);

        Assert.Equal(played, game.LastPlayedUtc);
        Assert.Equal(played, game.SteamAchievementsLastCheckedUtc);
        Assert.Equal(3, game.SteamAchievementsUnlocked);
        Assert.Equal(10, game.SteamAchievementsTotal);
    }

    [Fact]
    public void AchievementUpdate_WithoutATotalKeepsTheKnownProgress()
    {
        var game = new Game { SteamID = 620, SteamAchievementsUnlocked = 4, SteamAchievementsTotal = 10 };
        var played = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        new SteamAchievementUpdate(620, null, null, null, played).ApplyTo(game);

        Assert.Equal(played, game.LastPlayedUtc);
        Assert.Null(game.SteamAchievementsLastCheckedUtc);
        Assert.Equal(4, game.SteamAchievementsUnlocked);
        Assert.Equal(10, game.SteamAchievementsTotal);
    }
}
