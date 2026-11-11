using Codec.Models;
using Codec.Infrastructure;
using Codec.Services.Importing;
using Codec.Services.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.ViewModels;

public partial class MainViewModel
{
    private const int MaxBackgroundSyncFailures = 3;

    private readonly SemaphoreSlim _steamSyncGate = new(1, 1);
    private CancellationTokenSource? _steamLoginCts;
    private SteamSyncProgress? _pendingSteamProgress;
    private int _steamProgressUpdateQueued;
    private int _steamSyncGeneration;
    private volatile bool _steamProgressFinalized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSteamConnected))]
    [NotifyPropertyChangedFor(nameof(SteamAccountDisplay))]
    [NotifyPropertyChangedFor(nameof(SteamStatusCaption))]
    [NotifyPropertyChangedFor(nameof(HasSteamStatusCaption))]
    private string? _steamAccountName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamSyncButtonText))]
    [NotifyPropertyChangedFor(nameof(AreSteamActionsEnabled))]
    private bool _isSteamSyncing;
    [ObservableProperty] private bool _isSteamQrVisible;
    [ObservableProperty] private byte[]? _steamQrCodePng;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamStatusCaption))]
    [NotifyPropertyChangedFor(nameof(HasSteamStatusCaption))]
    private bool _isSteamSyncProgressVisible;
    [ObservableProperty] private bool _isSteamSyncProgressIndeterminate = true;
    [ObservableProperty] private string _steamSyncProgressTitle = "Syncing Steam library";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamStatusCaption))]
    private string _steamSyncProgressMessage = "Looking for new owned games";
    [ObservableProperty] private int _steamSyncProgressValue;
    [ObservableProperty] private int _steamSyncProgressMaximum = 1;

    public bool IsSteamConnected => !string.IsNullOrWhiteSpace(SteamAccountName) && _services.SteamLibrary.HasStoredToken;
    public string SteamAccountDisplay => IsSteamConnected ? SteamAccountName! : "Not connected";
    public string SteamSyncButtonText => IsSteamSyncing ? "Syncing..." : "Sync now";
    public bool AreSteamActionsEnabled => !IsSteamSyncing;

    /// <summary>
    /// The line under the account name. It says what the connection is doing while a sync
    /// runs and, the rest of the time, that there is one at all.
    /// </summary>
    public string SteamStatusCaption => IsSteamSyncProgressVisible
        ? SteamSyncProgressMessage
        : IsSteamConnected ? "Connected" : string.Empty;

    public bool HasSteamStatusCaption => SteamStatusCaption.Length > 0;

    private void InitializeSteamIntegration()
    {
        _services.SteamLibrary.QrCodeChanged += SteamLibrary_QrCodeChanged;
        SteamAccountName = _appSettings.SteamAccountName;
        OnPropertyChanged(nameof(IsSteamConnected));
        OnPropertyChanged(nameof(SteamAccountDisplay));
        OnPropertyChanged(nameof(SteamStatusCaption));
        OnPropertyChanged(nameof(HasSteamStatusCaption));
    }

    private void SteamLibrary_QrCodeChanged(byte[] png)
    {
        _uiDispatcher.Post(() =>
        {
            SteamQrCodePng = png;
            IsSteamQrVisible = true;
        });
    }

    [RelayCommand]
    private Task ConnectSteamAsync() => SyncSteamLibraryCoreAsync(useQr: true, isBackground: false);

    [RelayCommand]
    private Task SyncSteamLibraryAsync() => SyncSteamLibraryCoreAsync(useQr: false, isBackground: false);

    [RelayCommand]
    private void CancelSteamSignIn() => _steamLoginCts?.Cancel();

    private async Task SyncSteamLibraryCoreAsync(bool useQr, bool isBackground)
    {
        if (!await _steamSyncGate.WaitAsync(0)) return;
        bool signOutAfterSync = false;
        _steamLoginCts = new CancellationTokenSource();
        int syncGeneration = Interlocked.Increment(ref _steamSyncGeneration);
        _steamProgressFinalized = false;
        IsSteamSyncing = true;
        IsSteamSyncProgressVisible = !isBackground;
        IsSteamSyncProgressIndeterminate = true;
        SteamSyncProgressTitle = "Syncing Steam library";
        SteamSyncProgressMessage = "Looking for new owned games";
        SteamSyncProgressValue = 0;
        SteamSyncProgressMaximum = 1;

        PauseSilentImageUpdate();
        try
        {
            // The sync and its enrichment run on the thread pool, so they get copies, not the bound games.
            List<Game> librarySnapshot = CreateDetachedLibrarySnapshot();
            SteamSyncResult result = await _services.SteamLibrary.SyncAsync(
                librarySnapshot,
                SteamAccountName,
                useQr,
                (game, ct) => EnrichSteamGameAsync(game, librarySnapshot, ct),
                PublishSteamGamesAsync,
                PublishSteamOwnershipUpdatesAsync,
                PublishSteamAchievementUpdatesAsync,
                _appSettings.SteamAchievementsRetryAfterUtc,
                MarkSteamConnectedAsync,
                QueueSteamProgress,
                _steamLoginCts.Token);
            _steamLoginCts.Token.ThrowIfCancellationRequested();

            FinalizeSteamProgress();
            SteamAccountName = result.AccountName;
            _appSettings.SteamAccountName = result.AccountName;
            _appSettings.SteamId64 = result.SteamId64;
            _appSettings.SteamAchievementsRetryAfterUtc = result.AchievementRetryAfterUtc;
            _appSettings.LastSteamSyncUtc = DateTime.UtcNow;
            _appSettings.SteamBackgroundSyncFailures = 0;
            // Signing in again answers the request, so it no longer waits to be dismissed.
            IsSteamSignInNeededVisible = false;
            IsSteamSyncProgressIndeterminate = false;
            SteamSyncProgressValue = SteamSyncProgressMaximum;
            SteamSyncProgressTitle = "Syncing Steam library";
            SteamSyncProgressMessage = "Looking for new owned games";
            IsSteamQrVisible = false;
            await _services.AppSettings.SaveAsync(_appSettings);
            await _services.LibraryStorage.SaveAsync(Games.ToList());
            OnPropertyChanged(nameof(IsSteamConnected));
            OnPropertyChanged(nameof(SteamAccountDisplay));
            OnPropertyChanged(nameof(SteamStatusCaption));
            OnPropertyChanged(nameof(HasSteamStatusCaption));
            if (Games.Count > 0 && !IsOnboardingOwningScreen)
                IsOnboardingVisible = false;
            if (!isBackground)
                _ = DismissSteamSyncProgressAsync(syncGeneration);
        }
        catch (OperationCanceledException)
        {
            FinalizeSteamProgress();
            // Games and ownership changes published before the stop are only in memory.
            try { await _services.LibraryStorage.SaveAsync(Games.ToList()); } catch (Exception saveEx) { Debug.WriteLine($"[Steam] Saving after an interrupted sync failed: {saveEx.Message}"); }
            SteamSyncProgressTitle = "Steam sync cancelled";
            SteamSyncProgressMessage = "No unfinished games were added";
            IsSteamSyncProgressIndeterminate = false;
            if (!isBackground)
                _ = DismissSteamSyncProgressAsync(syncGeneration);

            // Only the player's own cancel is free; a timeout surfacing as a cancel is a failure.
            if (isBackground && !_steamLoginCts.IsCancellationRequested)
                signOutAfterSync = await RecordBackgroundSyncFailureAsync();
        }
        catch (Exception ex)
        {
            FinalizeSteamProgress();
            try { await _services.LibraryStorage.SaveAsync(Games.ToList()); } catch (Exception saveEx) { Debug.WriteLine($"[Steam] Saving after an interrupted sync failed: {saveEx.Message}"); }
            Debug.WriteLine($"[Steam] Sync failed: {ex}");
            IsSteamQrVisible = false;
            SteamSyncProgressTitle = "Steam sync stopped";
            SteamSyncProgressMessage = ex.Message;
            IsSteamSyncProgressIndeterminate = false;
            if (!isBackground)
                _ = DismissSteamSyncProgressAsync(syncGeneration);
            else
                signOutAfterSync = await RecordBackgroundSyncFailureAsync();
        }
        finally
        {
            IsSteamSyncing = false;
            _steamLoginCts.Dispose();
            _steamLoginCts = null;
            ResumeSilentImageUpdate();
            _steamSyncGate.Release();

            // The library handlers stay quiet while a sync runs, so games published before a
            // cancel or a failure would otherwise stay off the shelf until something else refreshes.
            // Last, so a throw here cannot keep the gate shut.
            RefreshAvailableImportSources();
            RefreshSidebarFilteredGames();
            RefreshDisplayedGames();
            NotifySteamLibraryVisibilityChanged();
        }

        // After the gate is open again, so the sign-out runs on its own and not inside the sync.
        if (signOutAfterSync)
        {
            await DisconnectSteamAsync();
            IsSteamSignInNeededVisible = true;
        }
    }

    /// <summary>
    /// Background syncs fail silently, so a login Steam no longer accepts would leave
    /// "Connected" standing forever. Counts the failure and says whether it was the last
    /// one allowed.
    /// </summary>
    private async Task<bool> RecordBackgroundSyncFailureAsync()
    {
        _appSettings.SteamBackgroundSyncFailures++;
        if (_appSettings.SteamBackgroundSyncFailures >= MaxBackgroundSyncFailures)
            return true;

        await _services.AppSettings.SaveAsync(_appSettings);
        return false;
    }

    // Called by the sync service, which is free to be on the thread pool by then.
    private Task MarkSteamConnectedAsync(string accountName, ulong steamId64)
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            SteamAccountName = accountName;
            _appSettings.SteamAccountName = accountName;
            _appSettings.SteamId64 = steamId64;
            IsSteamQrVisible = false;
            OnPropertyChanged(nameof(IsSteamConnected));
            OnPropertyChanged(nameof(SteamAccountDisplay));
            OnPropertyChanged(nameof(SteamStatusCaption));
            OnPropertyChanged(nameof(HasSteamStatusCaption));
            RefreshAvailableImportSources();
            RefreshSidebarFilteredGames();
            RefreshDisplayedGames();
            NotifySteamLibraryVisibilityChanged();
            await _services.AppSettings.SaveAsync(_appSettings);
        });
    }

    [RelayCommand]
    public async Task DisconnectSteamAsync()
    {
        // A running sync would otherwise keep adding owned games and write the account back.
        _steamLoginCts?.Cancel();
        await _services.SteamLibrary.DeleteTokenAsync();

        SteamAccountName = null;
        _appSettings.SteamAccountName = null;
        _appSettings.LastSteamSyncUtc = null;
        _appSettings.SteamId64 = null;
        _appSettings.SteamAchievementsRetryAfterUtc = null;
        // The next sign-in starts with a clean slate, not one failure away from signing out.
        _appSettings.SteamBackgroundSyncFailures = 0;
        IsSteamSyncProgressVisible = false;
        await _services.AppSettings.SaveAsync(_appSettings);
        await _services.LibraryStorage.SaveAsync(Games.ToList());
        RefreshAvailableImportSources();
        RefreshSidebarFilteredGames();
        RefreshDisplayedGames();
        NotifySteamLibraryVisibilityChanged();
        OnPropertyChanged(nameof(IsSteamConnected));
        OnPropertyChanged(nameof(SteamAccountDisplay));
        OnPropertyChanged(nameof(SteamStatusCaption));
        OnPropertyChanged(nameof(HasSteamStatusCaption));
    }

    private void NotifySteamLibraryVisibilityChanged()
    {
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(IsEmptyLibrary));
        NotifyLibrarySummaryChanged();
    }

    private async Task<Game?> EnrichSteamGameAsync(
        Game game,
        IReadOnlyCollection<Game> librarySnapshot,
        CancellationToken cancellationToken)
    {
        var request = new GameImportRequest(
            game.Executable,
            game.FolderLocation,
            game.Name,
            game.ImportedFrom,
            game.SteamID,
            MetadataLookupName: game.MetadataLookupName);

        var snapshot = librarySnapshot.Where(item => item.Id != game.Id).ToList();
        GameImportResult result = await _services.GameImportPipeline
            .ImportAsync(request, snapshot, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != GameImportResultStatus.Added || result.Game == null)
            return null;

        Game enriched = result.Game;
        enriched.Id = game.Id;
        enriched.DateAdded = game.DateAdded;
        enriched.IsFavorite = game.IsFavorite;
        enriched.IsSteamOwned = true;
        enriched.IsInstalled = game.IsInstalled;
        enriched.SteamAppType = game.SteamAppType;
        enriched.SteamAchievementsUnlocked = game.SteamAchievementsUnlocked;
        enriched.SteamAchievementsTotal = game.SteamAchievementsTotal;
        enriched.SteamAchievementsLastCheckedUtc = game.SteamAchievementsLastCheckedUtc;
        enriched.LastPlayedUtc = game.LastPlayedUtc;
        return enriched.IsFullyImported && enriched.DisplayedAssetsReady ? enriched : null;
    }

    private Task PublishSteamAchievementUpdatesAsync(IReadOnlyList<SteamAchievementUpdate> updates)
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            foreach (SteamAchievementUpdate update in updates)
            {
                Game? game = Games.FirstOrDefault(item => item.SteamID == update.AppId);
                if (game != null)
                    update.ApplyTo(game);
            }

            await _services.LibraryStorage.SaveAsync(Games.ToList());
        }, UiDispatchPriority.Background);
    }

    private Task PublishSteamOwnershipUpdatesAsync(IReadOnlyList<SteamOwnershipUpdate> updates)
    {
        return _uiDispatcher.InvokeAsync(() =>
        {
            foreach (SteamOwnershipUpdate update in updates)
            {
                Game? game = Games.FirstOrDefault(item => item.Id == update.GameId);
                if (game != null)
                    update.ApplyTo(game);
            }
        }, UiDispatchPriority.Background);
    }

    private List<Game> CreateDetachedLibrarySnapshot()
        => Games.Select(game => game.CreateHydrationSnapshot()).ToList();

    private async Task RefreshSteamAchievementsMaintenanceAsync()
    {
        if (!IsSteamConnected || !_appSettings.SteamId64.HasValue ||
            !await _steamSyncGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            ulong steamId64 = _appSettings.SteamId64.Value;
            List<Game> librarySnapshot = CreateDetachedLibrarySnapshot();
            SteamAchievementRefreshResult result = await _services.SteamLibrary.RefreshAchievementsAsync(
                librarySnapshot,
                steamId64,
                _appSettings.SteamAchievementsRetryAfterUtc,
                PublishSteamAchievementUpdatesAsync);

            // Disconnecting while this ran cleared the account; do not write its state back.
            if (_appSettings.SteamId64 != steamId64)
                return;

            _appSettings.SteamAchievementsRetryAfterUtc = result.RetryAfterUtc;
            await _services.AppSettings.SaveAsync(_appSettings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Steam] Achievement maintenance failed: {ex.Message}");
        }
        finally
        {
            _steamSyncGate.Release();
        }
    }

    private Task PublishSteamGamesAsync(IReadOnlyList<SteamEnrichedGame> enrichedGames)
    {
        return _uiDispatcher.InvokeAsync(() =>
        {
            foreach (SteamEnrichedGame item in enrichedGames)
            {
                // The enriched copy was built from a snapshot taken before sign-in and from
                // providers that may have failed, so it only adds to the game, never blanks it.
                Game? existing = Games.FirstOrDefault(game => game.Id == item.Game.Id);
                if (existing != null)
                    existing.MergeFetchedMetadataFrom(item.Game);
                else if (item.IsNew)
                    InsertGameSorted(item.Game);
            }

            if (!IsOnboardingOwningScreen)
                IsOnboardingVisible = false;
        }, UiDispatchPriority.Background);
    }

    private void QueueSteamProgress(SteamSyncProgress progress)
    {
        if (_steamProgressFinalized)
            return;

        Volatile.Write(ref _pendingSteamProgress, progress);
        if (Interlocked.Exchange(ref _steamProgressUpdateQueued, 1) != 0)
            return;

        _uiDispatcher.Post(ApplyQueuedSteamProgress, UiDispatchPriority.Background);
    }

    private void ApplyQueuedSteamProgress()
    {
        SteamSyncProgress? latest = Interlocked.Exchange(ref _pendingSteamProgress, null);
        Interlocked.Exchange(ref _steamProgressUpdateQueued, 0);
        if (!_steamProgressFinalized && latest != null)
        {
            IsSteamSyncProgressIndeterminate = false;
            SteamSyncProgressMaximum = Math.Max(1, latest.TotalCount);
            SteamSyncProgressValue = latest.ProcessedCount;
            SteamSyncProgressTitle = "Syncing Steam library";
            SteamSyncProgressMessage = latest.Phase == SteamSyncPhase.Achievements
                ? "Updating achievements"
                : "Looking for new owned games";
        }

        SteamSyncProgress? pending = Volatile.Read(ref _pendingSteamProgress);
        if (pending != null)
            QueueSteamProgress(pending);
    }

    private void FinalizeSteamProgress()
    {
        _steamProgressFinalized = true;
        Interlocked.Exchange(ref _pendingSteamProgress, null);
    }

    private async Task DismissSteamSyncProgressAsync(int syncGeneration)
    {
        await Task.Delay(4000).ConfigureAwait(false);
        _uiDispatcher.Post(() =>
        {
            if (syncGeneration == _steamSyncGeneration && !IsSteamSyncing)
                IsSteamSyncProgressVisible = false;
        });
    }

}
