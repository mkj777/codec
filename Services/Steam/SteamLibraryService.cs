using Codec.Helpers;
using Codec.Models;
using Codec.Services.Scanning.Scanners;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Steam;

public enum SteamSyncPhase
{
    Enriching,
    Completed
}

public sealed record SteamSyncProgress(
    SteamSyncPhase Phase,
    int TotalCount,
    int ProcessedCount,
    int AddedCount,
    int FailedCount);

public sealed record SteamSyncResult(
    string AccountName,
    int AddedCount,
    int UpdatedCount,
    int OwnedCount,
    int FailedCount);

public sealed class SteamLibraryService
{
    private readonly SteamAuthService _auth;

    public SteamLibraryService(SteamAuthService auth) => _auth = auth;

    public event Action<byte[]>? QrCodeChanged
    {
        add => _auth.QrCodeChanged += value;
        remove => _auth.QrCodeChanged -= value;
    }

    public bool HasStoredToken => _auth.HasStoredToken;

    public async Task<SteamSyncResult> SyncAsync(
        IList<Game> library,
        string? accountName,
        bool useQr,
        Func<Game, CancellationToken, Task<Game?>> enrichGameAsync,
        Func<Game, bool, Task> publishGameAsync,
        Func<string, Task>? accountConnectedAsync = null,
        Action<SteamSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SteamAccountSnapshot snapshot = await _auth.SignInAndFetchAsync(accountName, useQr, cancellationToken);
        if (accountConnectedAsync != null)
            await accountConnectedAsync(snapshot.AccountName);

        IReadOnlyList<GameCandidate> installedCandidates = await new SteamScanner().ScanAsync(cancellationToken: cancellationToken);
        var installed = installedCandidates
            .Where(candidate => candidate.SteamAppId.HasValue)
            .ToDictionary(candidate => candidate.SteamAppId!.Value);

        int updated = 0;
        var toEnrich = new List<SteamEnrichmentWorkItem>();

        foreach (SteamOwnedApp owned in snapshot.Apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int appId = checked((int)owned.AppId);
            Game? game = library.FirstOrDefault(item => item.SteamID == appId);
            bool isInstalledThroughSteam = installed.TryGetValue(appId, out GameCandidate? candidate);
            bool hasNonSteamInstallation = game != null &&
                !game.IsSteamLaunchTarget &&
                SteamScanner.IsInstallFolderPopulated(game.FolderLocation);
            bool isInstalled = isInstalledThroughSteam || hasNonSteamInstallation;

            if (game == null)
            {
                game = new Game
                {
                    Name = owned.Name,
                    SteamID = appId,
                    ImportedFrom = PlatformSourceNames.Steam,
                    Executable = candidate?.ExecutableHintPath ?? string.Empty,
                    FolderLocation = candidate?.FolderPath ?? string.Empty,
                    MetadataLookupName = candidate?.MetadataLookupName,
                    IsSteamOwned = true,
                    IsInstalled = isInstalled,
                    SteamAppType = owned.AppType,
                    LibraryCapsuleUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                    LibraryHeroUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg",
                    SteamPageUrl = $"https://store.steampowered.com/app/{appId}/"
                };
                toEnrich.Add(new SteamEnrichmentWorkItem(game, IsNew: true));
                continue;
            }

            game.IsSteamOwned = true;
            game.IsInstalled = isInstalled;
            game.SteamAppType = owned.AppType;
            if (candidate != null && !hasNonSteamInstallation)
            {
                game.FolderLocation = candidate.FolderPath;
                game.Executable = candidate.ExecutableHintPath ?? string.Empty;
            }

            if (!game.IsFullyImported || !game.DisplayedAssetsReady)
            {
                Game staged = game.CreateHydrationSnapshot();
                staged.IsSteamOwned = true;
                staged.IsInstalled = isInstalled;
                staged.SteamAppType = owned.AppType;
                toEnrich.Add(new SteamEnrichmentWorkItem(staged, IsNew: false));
            }
            updated++;
        }

        int added = 0;
        int processed = 0;
        int failed = 0;
        progress?.Invoke(new SteamSyncProgress(SteamSyncPhase.Enriching, toEnrich.Count, 0, 0, 0));

        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 32
        };

        await Parallel.ForEachAsync(toEnrich, options, async (workItem, ct) =>
        {
            bool succeeded = false;
            try
            {
                Game? enriched = await enrichGameAsync(workItem.Game, ct).ConfigureAwait(false);
                if (enriched != null)
                {
                    await publishGameAsync(enriched, workItem.IsNew).ConfigureAwait(false);
                    if (workItem.IsNew)
                        Interlocked.Increment(ref added);
                    succeeded = true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteamLibrary] Enrichment failed for {workItem.Game.Name}: {ex.Message}");
            }
            finally
            {
                if (!succeeded)
                    Interlocked.Increment(ref failed);
                int completed = Interlocked.Increment(ref processed);
                progress?.Invoke(new SteamSyncProgress(
                    SteamSyncPhase.Enriching,
                    toEnrich.Count,
                    completed,
                    Volatile.Read(ref added),
                    Volatile.Read(ref failed)));
            }
        }).ConfigureAwait(false);

        progress?.Invoke(new SteamSyncProgress(SteamSyncPhase.Completed, toEnrich.Count, processed, added, failed));
        return new SteamSyncResult(snapshot.AccountName, added, updated, snapshot.Apps.Count, failed);
    }

    public Task DeleteTokenAsync() => _auth.DeleteTokenAsync();

    public static int RemoveOwnedOnlyGames(IList<Game> library)
    {
        Game[] removable = library.Where(game => game.IsSteamOwned && !game.IsInstalled).ToArray();
        foreach (Game game in removable)
            library.Remove(game);
        return removable.Length;
    }

    private sealed record SteamEnrichmentWorkItem(Game Game, bool IsNew);
}
