using Codec.Models;
using Codec.Services.Importing;
using Codec.Services.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace Codec.ViewModels;

public partial class MainViewModel
{
    private readonly SemaphoreSlim _steamSyncGate = new(1, 1);
    private CancellationTokenSource? _steamLoginCts;
    private SteamSyncProgress? _pendingSteamProgress;
    private int _steamProgressUpdateQueued;
    private int _steamSyncGeneration;
    private volatile bool _steamProgressFinalized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSteamConnected))]
    [NotifyPropertyChangedFor(nameof(SteamAccountDisplay))]
    private string? _steamAccountName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamSyncButtonText))]
    [NotifyPropertyChangedFor(nameof(AreSteamActionsEnabled))]
    private bool _isSteamSyncing;
    [ObservableProperty] private bool _isSteamQrVisible;
    [ObservableProperty] private ImageSource? _steamQrCode;
    [ObservableProperty] private bool _isSteamSyncProgressVisible;
    [ObservableProperty] private bool _isSteamSyncProgressIndeterminate = true;
    [ObservableProperty] private string _steamSyncProgressTitle = "Syncing Steam library";
    [ObservableProperty] private string _steamSyncProgressMessage = "Looking for new owned games";
    [ObservableProperty] private int _steamSyncProgressValue;
    [ObservableProperty] private int _steamSyncProgressMaximum = 1;

    public bool IsSteamConnected => !string.IsNullOrWhiteSpace(SteamAccountName) && _services.SteamLibrary.HasStoredToken;
    public string SteamAccountDisplay => IsSteamConnected ? SteamAccountName! : "Not connected";
    public string SteamSyncButtonText => IsSteamSyncing ? "Syncing..." : "Sync now";
    public bool AreSteamActionsEnabled => !IsSteamSyncing;

    private void InitializeSteamIntegration()
    {
        _services.SteamLibrary.QrCodeChanged += SteamLibrary_QrCodeChanged;
        SteamAccountName = _appSettings.SteamAccountName;
        OnPropertyChanged(nameof(IsSteamConnected));
        OnPropertyChanged(nameof(SteamAccountDisplay));
    }

    private void SteamLibrary_QrCodeChanged(byte[] png)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            SteamQrCode = await CreateImageSourceAsync(png);
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
            IReadOnlyCollection<Game> librarySnapshot = Games.ToList();
            SteamSyncResult result = await _services.SteamLibrary.SyncAsync(
                Games,
                SteamAccountName,
                useQr,
                (game, ct) => EnrichSteamGameAsync(game, librarySnapshot, ct),
                PublishSteamGamesAsync,
                PublishSteamAchievementUpdatesAsync,
                _appSettings.SteamAchievementsRetryAfterUtc,
                MarkSteamConnectedAsync,
                QueueSteamProgress,
                _steamLoginCts.Token);

            FinalizeSteamProgress();
            SteamAccountName = result.AccountName;
            _appSettings.SteamAccountName = result.AccountName;
            _appSettings.SteamId64 = result.SteamId64;
            _appSettings.SteamAchievementsRetryAfterUtc = result.AchievementRetryAfterUtc;
            _appSettings.LastSteamSyncUtc = DateTime.UtcNow;
            IsSteamSyncProgressIndeterminate = false;
            SteamSyncProgressValue = SteamSyncProgressMaximum;
            SteamSyncProgressTitle = "Syncing Steam library";
            SteamSyncProgressMessage = "Looking for new owned games";
            IsSteamQrVisible = false;
            await _services.AppSettings.SaveAsync(_appSettings);
            await _services.LibraryStorage.SaveAsync(Games.ToList());
            RefreshAvailableImportSources();
            RefreshSidebarFilteredGames();
            RefreshDisplayedGames();
            NotifySteamLibraryVisibilityChanged();
            OnPropertyChanged(nameof(IsSteamConnected));
            OnPropertyChanged(nameof(SteamAccountDisplay));
            if (Games.Count > 0)
                IsOnboardingVisible = false;
            if (!isBackground)
                _ = DismissSteamSyncProgressAsync(syncGeneration);
        }
        catch (OperationCanceledException)
        {
            FinalizeSteamProgress();
            SteamSyncProgressTitle = "Steam sync cancelled";
            SteamSyncProgressMessage = "No unfinished games were added";
            IsSteamSyncProgressIndeterminate = false;
            if (!isBackground)
                _ = DismissSteamSyncProgressAsync(syncGeneration);
        }
        catch (Exception ex)
        {
            FinalizeSteamProgress();
            Debug.WriteLine($"[Steam] Sync failed: {ex}");
            IsSteamQrVisible = false;
            SteamSyncProgressTitle = "Steam sync stopped";
            SteamSyncProgressMessage = ex.Message;
            IsSteamSyncProgressIndeterminate = false;
            if (!isBackground)
                _ = DismissSteamSyncProgressAsync(syncGeneration);
        }
        finally
        {
            IsSteamSyncing = false;
            _steamLoginCts.Dispose();
            _steamLoginCts = null;
            ResumeSilentImageUpdate();
            _steamSyncGate.Release();
        }
    }

    private async Task MarkSteamConnectedAsync(string accountName, ulong steamId64)
    {
        SteamAccountName = accountName;
        _appSettings.SteamAccountName = accountName;
        _appSettings.SteamId64 = steamId64;
        IsSteamQrVisible = false;
        OnPropertyChanged(nameof(IsSteamConnected));
        OnPropertyChanged(nameof(SteamAccountDisplay));
        RefreshAvailableImportSources();
        RefreshSidebarFilteredGames();
        RefreshDisplayedGames();
        NotifySteamLibraryVisibilityChanged();
        await _services.AppSettings.SaveAsync(_appSettings);
    }

    public async Task DisconnectSteamAsync()
    {
        await _services.SteamLibrary.DeleteTokenAsync();

        SteamAccountName = null;
        _appSettings.SteamAccountName = null;
        _appSettings.LastSteamSyncUtc = null;
        _appSettings.SteamId64 = null;
        _appSettings.SteamAchievementsRetryAfterUtc = null;
        IsSteamSyncProgressVisible = false;
        await _services.AppSettings.SaveAsync(_appSettings);
        await _services.LibraryStorage.SaveAsync(Games.ToList());
        RefreshAvailableImportSources();
        RefreshSidebarFilteredGames();
        RefreshDisplayedGames();
        NotifySteamLibraryVisibilityChanged();
        OnPropertyChanged(nameof(IsSteamConnected));
        OnPropertyChanged(nameof(SteamAccountDisplay));
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
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            try
            {
                foreach (SteamAchievementUpdate update in updates)
                {
                    Game? game = Games.FirstOrDefault(item => item.SteamID == update.AppId);
                    if (game == null)
                        continue;

                    if (update.LastPlayedUtc.HasValue &&
                        (!game.LastPlayedUtc.HasValue || update.LastPlayedUtc > game.LastPlayedUtc))
                    {
                        game.LastPlayedUtc = update.LastPlayedUtc;
                    }

                    if (update.CheckedUtc.HasValue)
                        game.SteamAchievementsLastCheckedUtc = update.CheckedUtc;
                    if (update.TotalCount is > 0 && update.UnlockedCount.HasValue)
                    {
                        game.SteamAchievementsUnlocked = update.UnlockedCount;
                        game.SteamAchievementsTotal = update.TotalCount;
                    }
                }

                await _services.LibraryStorage.SaveAsync(Games.ToList());
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
        {
            completion.TrySetException(new InvalidOperationException("Could not publish Steam achievements."));
        }

        return completion.Task;
    }

    private async Task RefreshSteamAchievementsMaintenanceAsync()
    {
        if (!IsSteamConnected || !_appSettings.SteamId64.HasValue ||
            !await _steamSyncGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            List<Game> librarySnapshot = Games.ToList();
            SteamAchievementRefreshResult result = await _services.SteamLibrary.RefreshAchievementsAsync(
                librarySnapshot,
                _appSettings.SteamId64.Value,
                _appSettings.SteamAchievementsRetryAfterUtc,
                PublishSteamAchievementUpdatesAsync);
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
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                foreach (SteamEnrichedGame item in enrichedGames)
                {
                    Game? existing = Games.FirstOrDefault(game => game.Id == item.Game.Id);
                    if (existing != null)
                        existing.ApplyHydrationSnapshot(item.Game);
                    else if (item.IsNew)
                        InsertGameSorted(item.Game);
                }

                IsOnboardingVisible = false;
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
        {
            completion.TrySetException(new InvalidOperationException("Could not publish Steam games."));
        }

        return completion.Task;
    }

    private void QueueSteamProgress(SteamSyncProgress progress)
    {
        if (_steamProgressFinalized)
            return;

        Volatile.Write(ref _pendingSteamProgress, progress);
        if (Interlocked.Exchange(ref _steamProgressUpdateQueued, 1) != 0)
            return;

        if (!_dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyQueuedSteamProgress))
            Interlocked.Exchange(ref _steamProgressUpdateQueued, 0);
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
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (syncGeneration == _steamSyncGeneration && !IsSteamSyncing)
                IsSteamSyncProgressVisible = false;
        });
    }

    private static async Task<ImageSource> CreateImageSourceAsync(byte[] png)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }
}
