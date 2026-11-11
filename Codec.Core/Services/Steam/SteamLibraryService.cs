using Codec.Helpers;
using Codec.Models;
using Codec.Services.Scanning.Scanners;
using Codec.Services.Scanning;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Steam;

public enum SteamSyncPhase
{
    Achievements,
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
    ulong SteamId64,
    int AddedCount,
    int UpdatedCount,
    int OwnedCount,
    int FailedCount,
    DateTime? AchievementRetryAfterUtc);

public sealed record SteamEnrichedGame(Game Game, bool IsNew);

/// <summary>
/// What a sync learned about a game already in the library. Null paths mean the install
/// location stays as it is.
/// </summary>
public sealed record SteamOwnershipUpdate(
    Guid GameId,
    bool IsInstalled,
    string? SteamAppType,
    string? FolderLocation,
    string? Executable)
{
    public void ApplyTo(Game game)
    {
        game.IsSteamOwned = true;
        game.IsInstalled = IsInstalled;
        game.SteamAppType = SteamAppType;
        if (FolderLocation != null)
            game.FolderLocation = FolderLocation;
        // A player-chosen executable outranks whatever Steam reports for the install.
        if (Executable != null && !game.UseExecutableOverride)
            game.Executable = Executable;
    }
}

public sealed record SteamAchievementUpdate(
    int AppId,
    int? UnlockedCount,
    int? TotalCount,
    DateTime? CheckedUtc,
    DateTime? LastPlayedUtc)
{
    public void ApplyTo(Game game)
    {
        if (LastPlayedUtc.HasValue &&
            (!game.LastPlayedUtc.HasValue || LastPlayedUtc > game.LastPlayedUtc))
        {
            game.LastPlayedUtc = LastPlayedUtc;
        }

        if (CheckedUtc.HasValue)
            game.SteamAchievementsLastCheckedUtc = CheckedUtc;
        if (TotalCount is > 0 && UnlockedCount.HasValue)
        {
            game.SteamAchievementsUnlocked = UnlockedCount;
            game.SteamAchievementsTotal = TotalCount;
        }
    }
}

public sealed record SteamAchievementRefreshResult(DateTime? RetryAfterUtc, bool HasPendingWork);

/// <summary>
/// The owned apps matched against the library. Games are the service's own copies, never
/// the caller's, so it can read and change them on any thread.
/// </summary>
internal sealed record SteamOwnedReconciliation(
    IReadOnlyList<(Game Game, bool IsNew)> Games,
    IReadOnlyList<SteamOwnershipUpdate> OwnershipUpdates,
    int UpdatedCount);

public sealed class SteamLibraryService
{
    private static readonly HttpClient AchievementHttpClient = CreateAchievementHttpClient();
    private static readonly Regex AchievementSummaryPattern = new(
        @"id\s*=\s*[""']topSummaryAchievements[""'][^>]*>\s*(?:<div[^>]*>\s*)?(?<unlocked>[\d,]+)\s+of\s+(?<total>[\d,]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly TimeSpan AchievementRequestInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan AchievementThrottleCooldown = TimeSpan.FromMinutes(30);
    private readonly SteamAuthService _auth;
    private readonly ScanConcurrencyOptions _concurrency;

    public SteamLibraryService(SteamAuthService auth, ScanConcurrencyOptions concurrency)
    {
        _auth = auth;
        _concurrency = concurrency;
    }

    public event Action<byte[]>? QrCodeChanged
    {
        add => _auth.QrCodeChanged += value;
        remove => _auth.QrCodeChanged -= value;
    }

    public bool HasStoredToken => _auth.HasStoredToken;

    /// <summary>
    /// Syncs the owned Steam library. <paramref name="library"/> must be detached copies taken
    /// on the UI thread: this runs on the thread pool and only reads them. Every change reaches
    /// the caller through the publish callbacks, never through the games it was handed.
    /// </summary>
    public async Task<SteamSyncResult> SyncAsync(
        IReadOnlyList<Game> library,
        string? accountName,
        bool useQr,
        Func<Game, CancellationToken, Task<Game?>> enrichGameAsync,
        Func<IReadOnlyList<SteamEnrichedGame>, Task> publishGamesAsync,
        Func<IReadOnlyList<SteamOwnershipUpdate>, Task> publishOwnershipUpdatesAsync,
        Func<IReadOnlyList<SteamAchievementUpdate>, Task> publishAchievementUpdatesAsync,
        DateTime? achievementRetryAfterUtc,
        Func<string, ulong, Task>? accountConnectedAsync = null,
        Action<SteamSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SteamAccountSnapshot snapshot = await _auth.SignInAndFetchAsync(accountName, useQr, cancellationToken);
        if (accountConnectedAsync != null)
            await accountConnectedAsync(snapshot.AccountName, snapshot.SteamId64);

        var scanner = new SteamScanner();
        IReadOnlyList<GameCandidate> installedCandidates = await scanner.ScanAsync(cancellationToken: cancellationToken);
        var installed = installedCandidates
            .Where(candidate => candidate.SteamAppId.HasValue)
            .ToDictionary(candidate => candidate.SteamAppId!.Value);
        IReadOnlyDictionary<int, DateTime> lastPlayed = await scanner
            .ReadLastPlayedAsync(snapshot.SteamId64, cancellationToken)
            .ConfigureAwait(false);

        SteamOwnedReconciliation reconciliation = ReconcileOwnedApps(
            library,
            snapshot.Apps,
            installed,
            SteamScanner.IsInstallFolderPopulated,
            cancellationToken);
        IReadOnlyList<(Game Game, bool IsNew)> syncedGames = reconciliation.Games;
        int updated = reconciliation.UpdatedCount;

        foreach (SteamOwnershipUpdate[] batch in reconciliation.OwnershipUpdates.Chunk(10))
            await publishOwnershipUpdatesAsync(batch).ConfigureAwait(false);

        // The caller gets its own copy of each new game, so the one kept here stays off the UI.
        foreach (SteamEnrichedGame[] batch in syncedGames
            .Where(item => item.IsNew)
            .Select(item => new SteamEnrichedGame(item.Game.CreateHydrationSnapshot(), IsNew: true))
            .Chunk(10))
        {
            await publishGamesAsync(batch).ConfigureAwait(false);
        }

        var syncedByAppId = new Dictionary<int, Game>();
        foreach ((Game game, _) in syncedGames)
            syncedByAppId.TryAdd(game.SteamID!.Value, game);

        // Enrichment copies achievement progress from these games, so they follow what was published.
        async Task PublishAndTrackAchievementUpdatesAsync(IReadOnlyList<SteamAchievementUpdate> updates)
        {
            foreach (SteamAchievementUpdate update in updates)
            {
                if (syncedByAppId.TryGetValue(update.AppId, out Game? game))
                    update.ApplyTo(game);
            }

            await publishAchievementUpdatesAsync(updates).ConfigureAwait(false);
        }

        SteamAchievementRefreshResult achievementRefresh = await RefreshAchievementProgressAsync(
            snapshot.SteamId64,
            syncedGames.Select(item => item.Game).Where(TakesBackgroundWork).ToList(),
            installed.Keys.ToHashSet(),
            lastPlayed,
            achievementRetryAfterUtc,
            PublishAndTrackAchievementUpdatesAsync,
            progress,
            cancellationToken).ConfigureAwait(false);

        var toEnrich = syncedGames
            .Where(item => NeedsEnrichment(item.Game))
            .Select(item => new SteamEnrichmentWorkItem(item.Game.CreateHydrationSnapshot(), item.IsNew))
            .ToList();

        int added = 0;
        int processed = 0;
        int failed = 0;
        var enrichedGames = new ConcurrentQueue<SteamEnrichedGame>();
        progress?.Invoke(new SteamSyncProgress(SteamSyncPhase.Enriching, toEnrich.Count, 0, 0, 0));

        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = _concurrency.BackgroundWorkers
        };

        await Parallel.ForEachAsync(toEnrich, options, async (workItem, ct) =>
        {
            bool succeeded = false;
            try
            {
                Game? enriched = await enrichGameAsync(workItem.Game, ct).ConfigureAwait(false);
                if (enriched != null)
                {
                    enrichedGames.Enqueue(new SteamEnrichedGame(enriched, workItem.IsNew));
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

        foreach (SteamEnrichedGame[] batch in enrichedGames.Chunk(8))
            await publishGamesAsync(batch).ConfigureAwait(false);

        progress?.Invoke(new SteamSyncProgress(SteamSyncPhase.Completed, toEnrich.Count, processed, added, failed));
        return new SteamSyncResult(
            snapshot.AccountName,
            snapshot.SteamId64,
            added,
            updated,
            snapshot.Apps.Count,
            failed,
            achievementRefresh.RetryAfterUtc);
    }

    public Task DeleteTokenAsync() => _auth.DeleteTokenAsync();

    // A hidden game still has its ownership kept current, so it is right when restored, but
    // nothing goes to the network for it until then.
    private static bool TakesBackgroundWork(Game game) => !game.IsHidden;

    internal static bool NeedsEnrichment(Game game)
        => TakesBackgroundWork(game) && (!game.IsFullyImported || !game.DisplayedAssetsReady);

    /// <summary>
    /// Matches owned apps against the library without touching the games it is given:
    /// existing games are copied before they change, and the change is returned as an update.
    /// </summary>
    internal static SteamOwnedReconciliation ReconcileOwnedApps(
        IReadOnlyList<Game> library,
        IReadOnlyList<SteamOwnedApp> ownedApps,
        IReadOnlyDictionary<int, GameCandidate> installed,
        Func<string, bool> isInstallFolderPopulated,
        CancellationToken cancellationToken = default)
    {
        var librarySnapshot = library.ToList();
        var bySteamId = new Dictionary<int, Game>();
        foreach (Game game in librarySnapshot)
        {
            if (game.SteamID.HasValue)
                bySteamId.TryAdd(game.SteamID.Value, game);
        }

        int updated = 0;
        var syncedGames = new List<(Game Game, bool IsNew)>();
        var ownershipUpdates = new List<SteamOwnershipUpdate>();

        foreach (SteamOwnedApp owned in ownedApps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int appId = checked((int)owned.AppId);
            bySteamId.TryGetValue(appId, out Game? existing);
            bool isInstalledThroughSteam = installed.TryGetValue(appId, out GameCandidate? candidate);
            bool hasNonSteamInstallation = existing != null &&
                !existing.IsSteamLaunchTarget &&
                isInstallFolderPopulated(existing.FolderLocation);
            bool isInstalled = isInstalledThroughSteam || hasNonSteamInstallation;

            if (existing == null)
            {
                syncedGames.Add((new Game
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
                }, true));
                continue;
            }

            bool movesInstall = candidate != null && !hasNonSteamInstallation;
            var update = new SteamOwnershipUpdate(
                existing.Id,
                isInstalled,
                owned.AppType,
                movesInstall ? candidate!.FolderPath : null,
                movesInstall ? candidate!.ExecutableHintPath ?? string.Empty : null);

            Game working = existing.CreateHydrationSnapshot();
            update.ApplyTo(working);
            if (!existing.IsSteamOwned ||
                existing.IsInstalled != working.IsInstalled ||
                existing.SteamAppType != working.SteamAppType ||
                existing.FolderLocation != working.FolderLocation ||
                existing.Executable != working.Executable)
            {
                ownershipUpdates.Add(update);
            }

            syncedGames.Add((working, false));
            updated++;
        }

        return new SteamOwnedReconciliation(syncedGames, ownershipUpdates, updated);
    }

    public async Task<SteamAchievementRefreshResult> RefreshAchievementsAsync(
        IReadOnlyList<Game> library,
        ulong steamId64,
        DateTime? retryAfterUtc,
        Func<IReadOnlyList<SteamAchievementUpdate>, Task> publishUpdatesAsync,
        Action<SteamSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scanner = new SteamScanner();
        IReadOnlyList<GameCandidate> installedCandidates = await scanner
            .ScanAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        HashSet<int> installedAppIds = installedCandidates
            .Where(candidate => candidate.SteamAppId.HasValue)
            .Select(candidate => candidate.SteamAppId!.Value)
            .ToHashSet();
        IReadOnlyDictionary<int, DateTime> lastPlayed = await scanner
            .ReadLastPlayedAsync(steamId64, cancellationToken)
            .ConfigureAwait(false);

        return await RefreshAchievementProgressAsync(
            steamId64,
            library.Where(game => game.IsSteamOwned && game.SteamID.HasValue && TakesBackgroundWork(game)).ToList(),
            installedAppIds,
            lastPlayed,
            retryAfterUtc,
            publishUpdatesAsync,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SteamAchievementRefreshResult> RefreshAchievementProgressAsync(
        ulong steamId64,
        IReadOnlyList<Game> games,
        HashSet<int> installedAppIds,
        IReadOnlyDictionary<int, DateTime> localLastPlayed,
        DateTime? retryAfterUtc,
        Func<IReadOnlyList<SteamAchievementUpdate>, Task> publishUpdatesAsync,
        Action<SteamSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var activityUpdates = games
            .Where(game => game.SteamID.HasValue &&
                localLastPlayed.TryGetValue(game.SteamID.Value, out DateTime playedUtc) &&
                (!game.LastPlayedUtc.HasValue || playedUtc > game.LastPlayedUtc.Value))
            .Select(game => new SteamAchievementUpdate(
                game.SteamID!.Value,
                null,
                null,
                null,
                localLastPlayed[game.SteamID.Value]))
            .ToList();
        foreach (SteamAchievementUpdate[] batch in activityUpdates.Chunk(10))
            await publishUpdatesAsync(batch).ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        var dueGames = games
            .Where(game => game.SteamID.HasValue && IsAchievementRefreshDue(game, localLastPlayed, now))
            .OrderByDescending(game => GetEffectiveLastPlayed(game, localLastPlayed) >= now.AddDays(-3))
            .ThenByDescending(game => GetEffectiveLastPlayed(game, localLastPlayed) >= now.AddDays(-3)
                ? GetEffectiveLastPlayed(game, localLastPlayed)
                : null)
            .ThenByDescending(game => installedAppIds.Contains(game.SteamID!.Value))
            .ThenByDescending(game => !game.SteamAchievementsLastCheckedUtc.HasValue)
            .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (dueGames.Count == 0)
            return new SteamAchievementRefreshResult(null, HasPendingWork: false);
        if (retryAfterUtc > now)
            return new SteamAchievementRefreshResult(retryAfterUtc, HasPendingWork: true);

        progress?.Invoke(new SteamSyncProgress(SteamSyncPhase.Achievements, dueGames.Count, 0, 0, 0));
        var pendingUpdates = new List<SteamAchievementUpdate>(10);
        DateTime nextRequestUtc = DateTime.MinValue;
        int processed = 0;

        async Task FlushAsync()
        {
            if (pendingUpdates.Count == 0)
                return;
            SteamAchievementUpdate[] batch = pendingUpdates.ToArray();
            pendingUpdates.Clear();
            await publishUpdatesAsync(batch).ConfigureAwait(false);
        }

        foreach (Game game in dueGames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay = nextRequestUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            nextRequestUtc = DateTime.UtcNow + AchievementRequestInterval;

            AchievementFetchOutcome outcome;
            try
            {
                outcome = await FetchGameAchievementProgressAsync(
                    steamId64,
                    game.SteamID!.Value,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await FlushAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteamLibrary] Achievements unavailable for app {game.SteamID}: {ex.Message}");
                outcome = new AchievementFetchOutcome(AchievementFetchStatus.Failed, null, null);
            }

            processed++;
            progress?.Invoke(new SteamSyncProgress(
                SteamSyncPhase.Achievements,
                dueGames.Count,
                processed,
                0,
                0));

            if (outcome.Status == AchievementFetchStatus.Throttled)
            {
                await FlushAsync().ConfigureAwait(false);
                DateTime resumeUtc = outcome.RetryAfterUtc ?? DateTime.UtcNow + AchievementThrottleCooldown;
                Debug.WriteLine($"[SteamLibrary] Achievement requests throttled until {resumeUtc:u}");
                return new SteamAchievementRefreshResult(resumeUtc, HasPendingWork: true);
            }

            if (outcome.Status is AchievementFetchStatus.Success or AchievementFetchStatus.Unsupported)
            {
                DateTime checkedUtc = DateTime.UtcNow;
                pendingUpdates.Add(new SteamAchievementUpdate(
                    game.SteamID!.Value,
                    outcome.Progress?.UnlockedCount,
                    outcome.Progress?.TotalCount,
                    checkedUtc,
                    GetEffectiveLastPlayed(game, localLastPlayed)));
                if (pendingUpdates.Count == 10)
                    await FlushAsync().ConfigureAwait(false);
            }
        }

        await FlushAsync().ConfigureAwait(false);
        return new SteamAchievementRefreshResult(null, HasPendingWork: false);
    }

    private static bool IsAchievementRefreshDue(
        Game game,
        IReadOnlyDictionary<int, DateTime> localLastPlayed,
        DateTime now)
    {
        if (!game.SteamAchievementsLastCheckedUtc.HasValue)
            return true;

        DateTime checkedUtc = game.SteamAchievementsLastCheckedUtc.Value;
        DateTime? lastPlayedUtc = GetEffectiveLastPlayed(game, localLastPlayed);
        if (lastPlayedUtc > checkedUtc)
            return true;
        if (lastPlayedUtc >= now.AddDays(-1))
            return checkedUtc <= now.AddHours(-2);
        if (lastPlayedUtc >= now.AddDays(-3))
            return checkedUtc <= now.AddHours(-12);
        return !game.HasSteamAchievementProgress && checkedUtc <= now.AddDays(-30);
    }

    private static DateTime? GetEffectiveLastPlayed(
        Game game,
        IReadOnlyDictionary<int, DateTime> localLastPlayed)
    {
        DateTime? local = game.SteamID.HasValue && localLastPlayed.TryGetValue(game.SteamID.Value, out DateTime value)
            ? value
            : null;
        return !game.LastPlayedUtc.HasValue || local > game.LastPlayedUtc
            ? local
            : game.LastPlayedUtc;
    }

    private static async Task<AchievementFetchOutcome> FetchGameAchievementProgressAsync(
        ulong steamId64,
        int appId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://steamcommunity.com/profiles/{steamId64}/stats/{appId}/achievements/?l=english");
        using HttpResponseMessage response = await AchievementHttpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests || IsSteamThrottlePage(html))
        {
            DateTime? retryAfterUtc = response.Headers.RetryAfter?.Date?.UtcDateTime;
            if (!retryAfterUtc.HasValue && response.Headers.RetryAfter?.Delta is TimeSpan delta)
                retryAfterUtc = DateTime.UtcNow + delta;
            return new AchievementFetchOutcome(AchievementFetchStatus.Throttled, null, retryAfterUtc);
        }

        if (!response.IsSuccessStatusCode || IsPrivateProfilePage(html))
            return new AchievementFetchOutcome(AchievementFetchStatus.Failed, null, null);

        Match match = AchievementSummaryPattern.Match(html);
        if (match.Success &&
            int.TryParse(match.Groups["unlocked"].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int unlocked) &&
            int.TryParse(match.Groups["total"].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int total) &&
            total > 0 && unlocked <= total)
        {
            return new AchievementFetchOutcome(
                AchievementFetchStatus.Success,
                new SteamAchievementProgress(unlocked, total),
                null);
        }

        string finalPath = response.RequestMessage?.RequestUri?.AbsolutePath.Trim('/') ?? string.Empty;
        if (finalPath.Equals($"profiles/{steamId64}", StringComparison.OrdinalIgnoreCase))
            return new AchievementFetchOutcome(AchievementFetchStatus.Unsupported, null, null);

        Debug.WriteLine($"[SteamLibrary] Achievement summary unavailable for app {appId}");
        return new AchievementFetchOutcome(AchievementFetchStatus.Failed, null, null);
    }

    private static bool IsSteamThrottlePage(string html) =>
        html.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("Please wait and try your request again later", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateProfilePage(string html) =>
        html.Contains("This profile is private", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("profile is private", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateAchievementHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Codec/1.0");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        return client;
    }

    private sealed record SteamEnrichmentWorkItem(Game Game, bool IsNew);
    private sealed record SteamAchievementProgress(int UnlockedCount, int TotalCount);
    private sealed record AchievementFetchOutcome(
        AchievementFetchStatus Status,
        SteamAchievementProgress? Progress,
        DateTime? RetryAfterUtc);
    private enum AchievementFetchStatus { Success, Unsupported, Throttled, Failed }
}
