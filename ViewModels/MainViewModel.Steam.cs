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

    [ObservableProperty] private bool _isSteamSyncing;
    [ObservableProperty] private bool _isSteamQrVisible;
    [ObservableProperty] private ImageSource? _steamQrCode;
    [ObservableProperty] private string _steamStatusMessage = "Connect Steam to add everything you own.";
    [ObservableProperty] private string _steamLastSyncText = "Never synced";
    [ObservableProperty] private bool _isSteamSyncProgressVisible;
    [ObservableProperty] private bool _isSteamSyncProgressIndeterminate = true;
    [ObservableProperty] private string _steamSyncProgressTitle = "Checking Steam library…";
    [ObservableProperty] private string _steamSyncProgressMessage = "Finding owned games";
    [ObservableProperty] private int _steamSyncProgressValue;
    [ObservableProperty] private int _steamSyncProgressMaximum = 1;

    public bool IsSteamConnected => !string.IsNullOrWhiteSpace(SteamAccountName) && _services.SteamLibrary.HasStoredToken;
    public string SteamAccountDisplay => IsSteamConnected ? SteamAccountName! : "Not connected";

    private void InitializeSteamIntegration()
    {
        _services.SteamLibrary.QrCodeChanged += SteamLibrary_QrCodeChanged;
        SteamAccountName = _appSettings.SteamAccountName;
        SteamLastSyncText = _appSettings.LastSteamSyncUtc.HasValue
            ? $"Last synced {_appSettings.LastSteamSyncUtc.Value.ToLocalTime():g}"
            : "Never synced";
        OnPropertyChanged(nameof(IsSteamConnected));
        OnPropertyChanged(nameof(SteamAccountDisplay));
    }

    private void SteamLibrary_QrCodeChanged(byte[] png)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            SteamQrCode = await CreateImageSourceAsync(png);
            IsSteamQrVisible = true;
            SteamStatusMessage = "Scan with the Steam Mobile app and confirm the sign-in.";
        });
    }

    [RelayCommand]
    private Task ConnectSteamAsync() => SyncSteamLibraryCoreAsync(useQr: true);

    [RelayCommand]
    private Task SyncSteamLibraryAsync() => SyncSteamLibraryCoreAsync(useQr: false);

    [RelayCommand]
    private void CancelSteamSignIn() => _steamLoginCts?.Cancel();

    private async Task SyncSteamLibraryCoreAsync(bool useQr)
    {
        if (!await _steamSyncGate.WaitAsync(0)) return;
        _steamLoginCts = new CancellationTokenSource();
        int syncGeneration = Interlocked.Increment(ref _steamSyncGeneration);
        _steamProgressFinalized = false;
        IsSteamSyncing = true;
        SteamStatusMessage = useQr ? "Starting secure Steam sign-in…" : "Checking your Steam library…";
        IsSteamSyncProgressVisible = true;
        IsSteamSyncProgressIndeterminate = true;
        SteamSyncProgressTitle = "Checking Steam library…";
        SteamSyncProgressMessage = "Finding owned games";
        SteamSyncProgressValue = 0;
        SteamSyncProgressMaximum = 1;

        try
        {
            IReadOnlyCollection<Game> librarySnapshot = Games.ToList();
            SteamSyncResult result = await _services.SteamLibrary.SyncAsync(
                Games,
                SteamAccountName,
                useQr,
                (game, ct) => EnrichSteamGameAsync(game, librarySnapshot, ct),
                PublishSteamGameAsync,
                QueueSteamProgress,
                _steamLoginCts.Token);

            FinalizeSteamProgress();
            SteamAccountName = result.AccountName;
            _appSettings.SteamAccountName = result.AccountName;
            _appSettings.LastSteamSyncUtc = DateTime.UtcNow;
            SteamLastSyncText = $"Last synced {DateTime.Now:g}";
            int steamLibraryTotal = Games.Count(game =>
                game.IsSteamOwned && game.IsFullyImported && game.DisplayedAssetsReady);
            SteamStatusMessage = steamLibraryTotal == 1
                ? "1 game added through Steam Library"
                : $"{steamLibraryTotal} games added through Steam Library";
            IsSteamSyncProgressIndeterminate = false;
            SteamSyncProgressValue = SteamSyncProgressMaximum;
            SteamSyncProgressTitle = "Steam library ready";
            SteamSyncProgressMessage = result.FailedCount > 0
                ? $"{result.AddedCount} games added · {result.FailedCount} will retry"
                : $"{result.AddedCount} games added";
            IsSteamQrVisible = false;
            await _services.AppSettings.SaveAsync(_appSettings);
            await _services.LibraryStorage.SaveAsync(Games.ToList());
            RefreshAvailableImportSources();
            RefreshSidebarFilteredGames();
            RefreshDisplayedGames();
            OnPropertyChanged(nameof(IsSteamConnected));
            OnPropertyChanged(nameof(SteamAccountDisplay));
            if (Games.Count > 0)
                IsOnboardingVisible = false;
            _ = DismissSteamSyncProgressAsync(syncGeneration);
        }
        catch (OperationCanceledException)
        {
            FinalizeSteamProgress();
            SteamStatusMessage = "Steam sign-in was cancelled.";
            SteamSyncProgressTitle = "Steam sync cancelled";
            SteamSyncProgressMessage = "No unfinished games were added";
            IsSteamSyncProgressIndeterminate = false;
            _ = DismissSteamSyncProgressAsync(syncGeneration);
        }
        catch (Exception ex)
        {
            FinalizeSteamProgress();
            Debug.WriteLine($"[Steam] Sync failed: {ex}");
            SteamStatusMessage = ex.Message;
            IsSteamQrVisible = false;
            SteamSyncProgressTitle = "Steam sync stopped";
            SteamSyncProgressMessage = ex.Message;
            IsSteamSyncProgressIndeterminate = false;
            _ = DismissSteamSyncProgressAsync(syncGeneration);
        }
        finally
        {
            IsSteamSyncing = false;
            _steamLoginCts.Dispose();
            _steamLoginCts = null;
            _steamSyncGate.Release();
        }
    }

    public async Task DisconnectSteamAsync(bool removeOwnedOnlyGames)
    {
        await _services.SteamLibrary.DeleteTokenAsync();
        if (removeOwnedOnlyGames)
            SteamLibraryService.RemoveOwnedOnlyGames(Games);

        SteamAccountName = null;
        _appSettings.SteamAccountName = null;
        _appSettings.LastSteamSyncUtc = null;
        SteamLastSyncText = "Never synced";
        SteamStatusMessage = "Steam disconnected.";
        IsSteamSyncProgressVisible = false;
        await _services.AppSettings.SaveAsync(_appSettings);
        await _services.LibraryStorage.SaveAsync(Games.ToList());
        RefreshSidebarFilteredGames();
        RefreshDisplayedGames();
        OnPropertyChanged(nameof(IsSteamConnected));
        OnPropertyChanged(nameof(SteamAccountDisplay));
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
        return enriched.IsFullyImported && enriched.DisplayedAssetsReady ? enriched : null;
    }

    private Task PublishSteamGameAsync(Game enriched, bool isNew)
        => RunOnUiThreadAsync(() =>
        {
            Game? existing = Games.FirstOrDefault(game => game.Id == enriched.Id);
            if (existing != null)
            {
                existing.ApplyHydrationSnapshot(enriched);
                RefreshSidebarFilteredGames();
                RefreshDisplayedGames();
            }
            else if (isNew)
            {
                InsertGameSorted(enriched);
            }

            IsOnboardingVisible = false;
        });

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
            SteamSyncProgressMessage = $"{latest.AddedCount} games added · {latest.ProcessedCount} of {latest.TotalCount} prepared · Fetching details and artwork";
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
