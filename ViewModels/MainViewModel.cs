using Codec.Models;
using Codec.Services;
using Codec.Services.Importing;
using Codec.Services.Resolving;
using Codec.Services.Scanning;
using Codec.Services.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppSettings = Codec.Services.Storage.AppSettings;

namespace Codec.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private const int SidebarSearchDebounceDelayMs = 60;
        private const int UpdateNotificationDismissDelayMs = 15000;
        private static readonly StringComparer GameNameComparer = StringComparer.CurrentCultureIgnoreCase;

        private readonly DispatcherQueue _dispatcherQueue;
        private readonly ServiceHost _services;
        private readonly LibraryImportCoordinator _importCoordinator;
        private CancellationTokenSource? _sidebarSearchDebounceCts;
        private int _updateNotificationDismissVersion;
        private string _appliedSearchText = string.Empty;
        private AppSettings _appSettings = new();
        private bool _suppressSettingsSave = false;

        public ObservableCollection<Game> Games { get; set; } = new();
        public ObservableCollection<Game> SidebarFilteredGames { get; } = new();
        public ObservableCollection<Game> DisplayedGames { get; } = new();
        public ObservableCollection<Game> StartupCoverGames { get; } = new();
        public ObservableCollection<ImportFilterItem> AvailableImportSources { get; } = new();

        public bool HasStartupCovers => StartupCoverGames.Count > 0;

        [ObservableProperty]
        private string? _selectedImportFilter;

        [ObservableProperty]
        private int _selectedInstallFilter = 1;

        public MainViewModel(ServiceHost services)
        {
            _services = services;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _importCoordinator = new LibraryImportCoordinator(
                _services.GameImportPipeline,
                new GameScanner(_services.GameName, _services.Igdb, _services.ScanResources),
                GetLibrarySnapshotAsync,
                CommitImportedGameAsync,
                _services.ScanConcurrency);
            _importCoordinator.StatusChanged += ImportCoordinator_StatusChanged;
            _importCoordinator.NotificationRaised += ImportCoordinator_NotificationRaised;
            _services.Updates.StatusChanged += OnUpdateStatusChanged;
            OnUpdateStatusChanged(); // sync current state immediately (race-safe)
            Games.CollectionChanged += Games_CollectionChanged;
            RefreshSidebarFilteredGames();
            RefreshAvailableImportSources();
            RefreshDisplayedGames();
        }

        // ---------------------------------------------------------------------------------
        // Observable Properties
        // ---------------------------------------------------------------------------------

        public bool HasGames => Games.Count > 0;
        public bool IsEmptyLibrary => !HasGames;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLibraryGridVisible))]
        private bool _isDetailsVisible;

        public bool IsLibraryGridVisible => !IsDetailsVisible;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLibraryVisible))]
        private bool _isInitialLoading = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLibraryVisible))]
        private bool _isOnboardingVisible;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLibraryVisible))]
        private bool _isLoadingVisible;

        [ObservableProperty]
        private string _loadingTitle = "Finding your games...";

        [ObservableProperty]
        private string _loadingSubtitle = "This will take a few minutes...";

        [ObservableProperty]
        private Game? _selectedGame;

        public bool IsLibraryVisible => !IsInitialLoading && !IsOnboardingVisible && !IsLoadingVisible;

        [ObservableProperty]
        private bool _isScanning;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartScan))]
        private bool _isImportActive;

        public bool CanStartScan => !IsImportActive;

        [ObservableProperty] private bool _isScanProgressVisible;
        [ObservableProperty] private string _scanProgressMessage = string.Empty;
        [ObservableProperty] private bool _scanProgressIsIndeterminate = true;
        [ObservableProperty] private double _scanProgressValue;
        [ObservableProperty] private double _scanProgressMaximum = 1;
        [ObservableProperty] private double _scanProgressMinimum;

        [ObservableProperty] private bool _isAppSpinnerActive;
        [ObservableProperty] private bool _isGameSettingsOpen;
        [ObservableProperty] private bool _isMediaOverlayOpen;
        [ObservableProperty] private bool _isFranchiseOverlayOpen;
        [ObservableProperty] private List<FranchiseTimelineItem>? _franchiseTimelineItems;
        // 0 = Mainline only, 1 = Extended, 2 = All
        [ObservableProperty] private int _franchiseFilterMode;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FranchiseMainlineLabel))]
        private int _franchiseMainlineCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FranchiseExtendedLabel))]
        private int _franchiseExtendedCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FranchiseAllLabel))]
        private int _franchiseAllCount;

        public string FranchiseMainlineLabel => $"Mainline ({FranchiseMainlineCount})";
        public string FranchiseExtendedLabel => $"Extended ({FranchiseExtendedCount})";
        public string FranchiseAllLabel => $"All ({FranchiseAllCount})";
        [ObservableProperty] private bool _isSettingsVisible;
        [ObservableProperty] private bool _isResetConfirmVisible;
        [ObservableProperty]
        private bool _isSidebarCollapsed;
        [ObservableProperty] private bool _scanOnStartup;
        [ObservableProperty] private bool _launchSteamSilent;
        [ObservableProperty] private bool _isUiEnabled = true;
        [ObservableProperty] private bool _isImportStatusVisible;
        [ObservableProperty] private bool _isStartupScanToastVisible;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsScanCompleteEffectiveVisible))]
        private bool _isScanCompleteVisible;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsScanCompleteEffectiveVisible))]
        private bool _isGameNotAddedToastVisible;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsScanCompleteEffectiveVisible))]
        private bool _isGameAlreadyAddedToastVisible;

        public bool IsScanCompleteEffectiveVisible => IsScanCompleteVisible && !IsGameNotAddedToastVisible && !IsGameAlreadyAddedToastVisible;
        [ObservableProperty] private int _scanCompleteAddedCount;
        [ObservableProperty] private string _importStatusMessage = string.Empty;
        [ObservableProperty] private int _queuedCount;
        [ObservableProperty] private int _processingCount;
        [ObservableProperty] private int _addedCount;
        [ObservableProperty] private int _skippedCount;
        [ObservableProperty] private int _failedCount;
        [ObservableProperty] private int _importRemainingCount;
        [ObservableProperty] private Game? _sidebarSelectedItem;
        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private bool _isUpdateBannerVisible;
        [ObservableProperty] private bool _isUpdateCheckingVisible;
        [ObservableProperty] private bool _isUpdateDownloadingVisible;
        [ObservableProperty] private int _updateDownloadProgress;
        [ObservableProperty] private bool _isUpdateErrorVisible;
        [ObservableProperty] private string _updateErrorMessage = string.Empty;
        [ObservableProperty] private bool _isUpdateNoUpdateVisible;

        [ObservableProperty]
        private bool _isDebugMode =
#if DEBUG
            true;
#else
            false;
#endif

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SortLabel))]
        [NotifyPropertyChangedFor(nameof(SortIconGlyph))]
        [NotifyPropertyChangedFor(nameof(ShowFolderSizeTag))]
        [NotifyPropertyChangedFor(nameof(ShowDateAddedTag))]
        private int _selectedSortIndex = 0; // 0=Alphabetic, 1=FolderSize desc, 2=DateAdded desc

        public string SortLabel => SelectedSortIndex switch
        {
            0 => "Alphabetic",
            1 => "Alphabetic",
            2 => "Folder Size",
            3 => "Folder Size",
            4 => "Date Added",
            5 => "Date Added",
            _ => "Alphabetic",
        };

        public string SortIconGlyph => SelectedSortIndex switch
        {
            1 => "\uE74A", // Z-A ArrowUp
            3 => "\uE74A", // Folder Size (Small-Large) ArrowUp
            5 => "\uE74A", // Date Added (Oldest) ArrowUp
            _ => "\uE74B", // ArrowDown for all others (0, 2, 4)
        };

        public bool ShowFolderSizeTag => SelectedSortIndex == 2 || SelectedSortIndex == 3;
        public bool ShowDateAddedTag => SelectedSortIndex == 4 || SelectedSortIndex == 5;

        public void SetLoadingState(bool isVisible, string? title = null, string? subtitle = null)
        {
            if (!string.IsNullOrWhiteSpace(title))
                LoadingTitle = title;
            if (subtitle != null)
                LoadingSubtitle = subtitle;
            IsLoadingVisible = isVisible;
        }

        // ---------------------------------------------------------------------------------
        // Navigation
        // ---------------------------------------------------------------------------------

        [RelayCommand]
        private void RestartToUpdate()
        {
            _services.Updates.ApplyUpdateAndRestart();
        }

        private void OnUpdateStatusChanged()
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                var s = _services.Updates;
                IsUpdateCheckingVisible = false;
                IsUpdateDownloadingVisible = s.Status == UpdateStatus.Downloading;
                IsUpdateBannerVisible = s.Status == UpdateStatus.Ready;
                IsUpdateNoUpdateVisible = false;
                IsUpdateErrorVisible = s.Status == UpdateStatus.Error;
                UpdateDownloadProgress = s.DownloadProgress;
                UpdateErrorMessage = s.ErrorMessage ?? string.Empty;

                if (s.Status == UpdateStatus.Error)
                    ScheduleUpdateNotificationDismissal(s.Status);
                else
                    Interlocked.Increment(ref _updateNotificationDismissVersion);
            });
        }

        private void ScheduleUpdateNotificationDismissal(UpdateStatus status)
        {
            int version = Interlocked.Increment(ref _updateNotificationDismissVersion);
            _ = DismissUpdateNotificationAsync(status, version);
        }

        private async Task DismissUpdateNotificationAsync(UpdateStatus status, int version)
        {
            await Task.Delay(UpdateNotificationDismissDelayMs).ConfigureAwait(false);

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (version != _updateNotificationDismissVersion || _services.Updates.Status != status)
                    return;

                if (status == UpdateStatus.Error)
                    IsUpdateErrorVisible = false;
                else if (status == UpdateStatus.NoUpdateFound)
                    IsUpdateNoUpdateVisible = false;
            });
        }

        [RelayCommand]
        private void Back()
        {
            IsDetailsVisible = false;
            IsGameSettingsOpen = false;
            IsMediaOverlayOpen = false;
            IsFranchiseOverlayOpen = false;
            SelectedGame = null;
            SidebarSelectedItem = null;
        }

        [RelayCommand]
        private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

        // ---------------------------------------------------------------------------------
        // Library Lifecycle
        // ---------------------------------------------------------------------------------

        public async Task LoadLibraryAsync()
        {
            _services.LibraryStorage.EnsureStorageInitialized();

            _appSettings = await _services.AppSettings.LoadAsync();
            _suppressSettingsSave = true;
            ScanOnStartup = _appSettings.ScanOnStartup;
            LaunchSteamSilent = _appSettings.LaunchSteamSilent;
            SelectedSortIndex = _appSettings.SelectedSortIndex;
            IsSidebarCollapsed = _appSettings.IsSidebarCollapsed;
            _suppressSettingsSave = false;
            InitializeSteamIntegration();

            var saved = await _services.LibraryStorage.LoadAsync();
            PrepareStartupCoverGames(saved);
            await EnsureCoversAsync(saved);
            var sortedSavedGames = GetSortedGames(saved).ToList();

            Games.Clear();
            foreach (var g in sortedSavedGames)
                Games.Add(g);

            await _services.LibraryStorage.SaveAsync(sortedSavedGames);
            QueueBackgroundPrefetch(Games);

            SetLoadingState(false);
            IsInitialLoading = false;
            IsOnboardingVisible = Games.Count == 0 && !_appSettings.OnboardingCompleted;

            if (_appSettings.OnboardingCompleted && _appSettings.ScanOnStartup)
                _ = ScanGamesOnStartupAsync();

            _ = SilentUpdateImagesAsync(Games);

            if (IsSteamConnected)
                _ = SyncSteamLibraryCoreAsync(useQr: false);
        }

        private void PrepareStartupCoverGames(IReadOnlyList<Game> games)
        {
            StartupCoverGames.Clear();
            var cachedGames = games.Where(game => HasLocalStartupCover(game.LibraryCapsuleCache)).Take(9).ToList();

            if (cachedGames.Count > 0)
            {
                for (int index = 0; index < 9; index++)
                    StartupCoverGames.Add(cachedGames[index % cachedGames.Count]);
            }

            OnPropertyChanged(nameof(HasStartupCovers));
        }

        private static bool HasLocalStartupCover(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
                    return File.Exists(uri.LocalPath);

                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        public async Task CompleteOnboardingAsync(bool scanOnStartup, bool launchSteamSilent)
        {
            _appSettings.OnboardingCompleted = true;
            _appSettings.ScanOnStartup = scanOnStartup;
            _appSettings.LaunchSteamSilent = launchSteamSilent;
            _suppressSettingsSave = true;
            ScanOnStartup = scanOnStartup;
            LaunchSteamSilent = launchSteamSilent;
            _suppressSettingsSave = false;
            await _services.AppSettings.SaveAsync(_appSettings);
        }

        [RelayCommand]
        private async Task FindGamesFromOnboardingAsync()
        {
            await CompleteOnboardingAsync(scanOnStartup: true, launchSteamSilent: false);
            IsOnboardingVisible = false;
            await ScanGamesAsync();
        }

        [RelayCommand]
        private async Task AddGameFromOnboardingAsync(string? executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return;

            ImportEnqueueResult result = await AddGameCommand(executablePath);
            if (!result.IsAccepted)
                return;

            await CompleteOnboardingAsync(scanOnStartup: true, launchSteamSilent: false);
            IsOnboardingVisible = false;
        }

        [RelayCommand]
        private async Task MaybeLaterFromOnboardingAsync()
        {
            await CompleteOnboardingAsync(scanOnStartup: true, launchSteamSilent: false);
            IsOnboardingVisible = false;
        }

        public Task CancelImportAsync() => _importCoordinator.CancelAndDrainAsync();

        partial void OnIsSidebarCollapsedChanged(bool value)
        {
            if (_suppressSettingsSave) return;
            _appSettings.IsSidebarCollapsed = value;
            _ = _services.AppSettings.SaveAsync(_appSettings);
        }

        partial void OnScanOnStartupChanged(bool value)
        {
            if (_suppressSettingsSave) return;
            _appSettings.ScanOnStartup = value;
            _ = _services.AppSettings.SaveAsync(_appSettings);
        }

        partial void OnLaunchSteamSilentChanged(bool value)
        {
            if (_suppressSettingsSave) return;
            _appSettings.LaunchSteamSilent = value;
            _ = _services.AppSettings.SaveAsync(_appSettings);
        }

        public string? TryLaunchSteamSilent()
        {
            string? path = _appSettings.SteamClientPath;
            if (string.IsNullOrEmpty(path))
                return "Steam client path not found. Run a game scan first.";
            if (!System.IO.File.Exists(path))
                return $"steam.exe not found at:\n{path}";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, "-silent") { UseShellExecute = true });
                return null;
            }
            catch (Exception ex)
            {
                return $"Failed to launch Steam: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task ScanGamesAsync()
        {
            IsOnboardingVisible = false;
            await _importCoordinator.StartScanAsync();
            IsOnboardingVisible = Games.Count == 0 && !IsImportStatusVisible && !_appSettings.OnboardingCompleted;
        }

        private async Task ScanGamesOnStartupAsync()
        {
            IsStartupScanToastVisible = true;
            await _importCoordinator.StartScanAsync();
        }

        public async Task<ImportEnqueueResult> AddGameCommand(string executablePath)
        {
            IsOnboardingVisible = false;
            var result = await _importCoordinator.EnqueueManualExecutableAsync(executablePath);
            if (!result.IsAccepted && Games.Count == 0)
            {
                IsOnboardingVisible = !_appSettings.OnboardingCompleted;
            }
            if (result.Status == ImportEnqueueResultStatus.Invalid)
            {
                IsGameNotAddedToastVisible = true;
                _ = DismissGameNotAddedToastAsync();
            }
            else if (result.Status == ImportEnqueueResultStatus.Duplicate)
            {
                IsGameAlreadyAddedToastVisible = true;
                _ = DismissGameAlreadyAddedToastAsync();
            }
            return result;
        }

        private async Task DismissGameNotAddedToastAsync()
        {
            await Task.Delay(4000).ConfigureAwait(false);
            _dispatcherQueue.TryEnqueue(() => IsGameNotAddedToastVisible = false);
        }

        private async Task DismissGameAlreadyAddedToastAsync()
        {
            await Task.Delay(4000).ConfigureAwait(false);
            _dispatcherQueue.TryEnqueue(() => IsGameAlreadyAddedToastVisible = false);
        }

        // ---------------------------------------------------------------------------------
        // Sidebar Search & Filter
        // ---------------------------------------------------------------------------------

        partial void OnSearchTextChanged(string value)
        {
            string normalizedSearchText = NormalizeSearchText(value);

            _sidebarSearchDebounceCts?.Cancel();
            _sidebarSearchDebounceCts?.Dispose();

            var debounceCts = new CancellationTokenSource();
            _sidebarSearchDebounceCts = debounceCts;
            _ = DebounceSidebarSearchAsync(normalizedSearchText, debounceCts);
        }

        private async Task DebounceSidebarSearchAsync(string searchText, CancellationTokenSource debounceCts)
        {
            try
            {
                await Task.Delay(SidebarSearchDebounceDelayMs, debounceCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            void ApplySearch()
            {
                if (!ReferenceEquals(_sidebarSearchDebounceCts, debounceCts))
                    return;

                _appliedSearchText = searchText;
                _sidebarSearchDebounceCts = null;
                debounceCts.Dispose();
                RefreshSidebarFilteredGames();
            }

            if (_dispatcherQueue.HasThreadAccess)
                ApplySearch();
            else
                _ = _dispatcherQueue.TryEnqueue(ApplySearch);
        }

        private void Games_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasGames));
            OnPropertyChanged(nameof(IsEmptyLibrary));
            OnPropertyChanged(nameof(IsLibraryVisible));
            RefreshSidebarFilteredGames();
            RefreshAvailableImportSources();
            RefreshDisplayedGames();
        }

        private bool _isUpdatingFilters = false;

        private void OnFilterItemSelectionChanged(ImportFilterItem changedItem)
        {
            if (_isUpdatingFilters) return;

            _isUpdatingFilters = true;
            try
            {
                if (changedItem.IsSelected)
                {
                    // Unselect others
                    foreach (var item in AvailableImportSources)
                    {
                        if (item != changedItem)
                        {
                            item.IsSelected = false;
                        }
                    }
                    SelectedImportFilter = changedItem.Name;
                }
                else
                {
                    // If we unselected the active filter, clear the filter name
                    if (SelectedImportFilter == changedItem.Name)
                    {
                        SelectedImportFilter = null;
                    }
                }
            }
            finally
            {
                _isUpdatingFilters = false;
            }

        }

        partial void OnSelectedImportFilterChanged(string? value)
        {
            RefreshSidebarFilteredGames();
            RefreshDisplayedGames();
        }

        partial void OnSelectedInstallFilterChanged(int value)
        {
            RefreshSidebarFilteredGames();
            RefreshDisplayedGames();
        }

        [RelayCommand]
        private async Task ToggleFavoriteAsync(Game? game)
        {
            if (game == null)
                return;

            game.IsFavorite = !game.IsFavorite;
            ApplySortToGames();
            await _services.LibraryStorage.SaveAsync(Games.ToList());
        }

        private void RefreshAvailableImportSources()
        {
            var sources = Games
                .Select(g => g.ImportedFromDisplay)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList();

            var toRemove = AvailableImportSources.Where(item => !sources.Contains(item.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var item in toRemove)
            {
                AvailableImportSources.Remove(item);
            }

            foreach (var src in sources)
            {
                if (!AvailableImportSources.Any(item => string.Equals(item.Name, src, StringComparison.OrdinalIgnoreCase)))
                {
                    AvailableImportSources.Add(new ImportFilterItem(src, OnFilterItemSelectionChanged));
                }
            }

            if (SelectedImportFilter != null && !sources.Contains(SelectedImportFilter, StringComparer.OrdinalIgnoreCase))
            {
                SelectedImportFilter = null;
            }
        }

        private void RefreshDisplayedGames()
        {
            var sortedFiltered = GetSortedGames(Games.Where(MatchesActiveFilters)).ToList();

            for (int targetIndex = 0; targetIndex < sortedFiltered.Count; targetIndex++)
            {
                var game = sortedFiltered[targetIndex];
                int existingIndex = DisplayedGames.IndexOf(game);

                if (existingIndex == targetIndex)
                    continue;

                if (existingIndex >= 0)
                    DisplayedGames.Move(existingIndex, targetIndex);
                else
                    DisplayedGames.Insert(targetIndex, game);
            }

            for (int index = DisplayedGames.Count - 1; index >= sortedFiltered.Count; index--)
                DisplayedGames.RemoveAt(index);

        }

        private void RefreshSidebarFilteredGames()
        {
            var filteredGames = Games
                .Where(IsReadyForLibrary)
                .Where(MatchesImportFilter)
                .Where(game => !string.IsNullOrWhiteSpace(_appliedSearchText) || MatchesInstallFilter(game))
                .Where(MatchesSidebarSearch)
                .OrderByDescending(game => game.IsFavorite)
                .ThenBy(game => game.Name ?? string.Empty, GameNameComparer)
                .ThenBy(game => game.Id)
                .ToList();

            for (int targetIndex = 0; targetIndex < filteredGames.Count; targetIndex++)
            {
                var game = filteredGames[targetIndex];
                int existingIndex = SidebarFilteredGames.IndexOf(game);

                if (existingIndex == targetIndex)
                    continue;

                if (existingIndex >= 0)
                    SidebarFilteredGames.Move(existingIndex, targetIndex);
                else
                    SidebarFilteredGames.Insert(targetIndex, game);
            }

            for (int index = SidebarFilteredGames.Count - 1; index >= filteredGames.Count; index--)
                SidebarFilteredGames.RemoveAt(index);

            if (SidebarSelectedItem != null && !filteredGames.Contains(SidebarSelectedItem))
                SidebarSelectedItem = null;

            if (!string.IsNullOrEmpty(_appliedSearchText) && filteredGames.Count > 0)
                SidebarSelectedItem = filteredGames[0];
        }

        private bool MatchesSidebarSearch(Game game)
        {
            if (string.IsNullOrWhiteSpace(_appliedSearchText))
                return true;

            return game.Name?.Contains(_appliedSearchText, StringComparison.OrdinalIgnoreCase) == true;
        }

        private bool MatchesActiveFilters(Game game)
        {
            return IsReadyForLibrary(game) && MatchesImportFilter(game) && MatchesInstallFilter(game);
        }

        private static bool IsReadyForLibrary(Game game)
            => !game.IsSteamOwned || (game.IsFullyImported && game.DisplayedAssetsReady);

        private bool MatchesImportFilter(Game game)
            => string.IsNullOrEmpty(SelectedImportFilter) ||
               string.Equals(game.ImportedFromDisplay, SelectedImportFilter, StringComparison.OrdinalIgnoreCase);

        private bool MatchesInstallFilter(Game game)
            => SelectedInstallFilter switch
            {
                1 => game.IsInstalled,
                2 => !game.IsInstalled,
                _ => true
            };

        partial void OnSelectedSortIndexChanged(int value)
        {
            ApplySortToGames();
            if (!_suppressSettingsSave)
            {
                _appSettings.SelectedSortIndex = value;
                _ = _services.AppSettings.SaveAsync(_appSettings);
            }
        }

        private IEnumerable<Game> GetSortedGames(IEnumerable<Game> source) => SelectedSortIndex switch
        {
            0 => source.OrderByDescending(g => g.IsFavorite).ThenBy(g => g.Name ?? string.Empty, GameNameComparer).ThenBy(g => g.Id),
            1 => source.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.Name ?? string.Empty, GameNameComparer).ThenBy(g => g.Id),
            2 => source.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.FolderSize).ThenBy(g => g.Id),
            3 => source.OrderByDescending(g => g.IsFavorite).ThenBy(g => g.FolderSize).ThenBy(g => g.Id),
            4 => source.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.DateAdded).ThenBy(g => g.Id),
            5 => source.OrderByDescending(g => g.IsFavorite).ThenBy(g => g.DateAdded).ThenBy(g => g.Id),
            _ => source.OrderByDescending(g => g.IsFavorite).ThenBy(g => g.Name ?? string.Empty, GameNameComparer).ThenBy(g => g.Id),
        };

        private void ApplySortToGames()
        {
            Games.CollectionChanged -= Games_CollectionChanged;
            var sorted = GetSortedGames(Games).ToList();
            Games.Clear();
            foreach (var g in sorted)
                Games.Add(g);
            Games.CollectionChanged += Games_CollectionChanged;
            OnPropertyChanged(nameof(HasGames));
            OnPropertyChanged(nameof(IsEmptyLibrary));
            OnPropertyChanged(nameof(IsLibraryVisible));
            RefreshSidebarFilteredGames();
            RefreshDisplayedGames();
        }

        private void InsertGameSorted(Game game)
        {
            if (SelectedSortIndex != 0)
            {
                Games.Add(game);
                ApplySortToGames();
                return;
            }
            int insertIndex = 0;
            while (insertIndex < Games.Count && CompareGamesByName(Games[insertIndex], game) <= 0)
                insertIndex++;
            Games.Insert(insertIndex, game);
        }

        private static int CompareGamesByName(Game left, Game right)
        {
            int favoriteComparison = right.IsFavorite.CompareTo(left.IsFavorite);
            if (favoriteComparison != 0)
                return favoriteComparison;

            int nameComparison = GameNameComparer.Compare(left.Name ?? string.Empty, right.Name ?? string.Empty);
            if (nameComparison != 0)
                return nameComparison;

            return left.Id.CompareTo(right.Id);
        }

        private static string NormalizeSearchText(string? value) => value?.Trim() ?? string.Empty;
    }

    public sealed class FranchiseTimelineItem
    {
        public FranchiseGameRef Entry { get; }
        public bool IsAbove { get; }
        public bool IsBelow => !IsAbove;
        public string ReleaseYearDisplay => Entry.ReleaseDate.HasValue
            ? Entry.ReleaseDate.Value.Year.ToString() : string.Empty;
        public IEnumerable<string> PlatformLogoUris => Game.GetPlatformLogoUris(Entry.Platforms);
        // Show badge for everything except main games
        public string? BadgeText => string.IsNullOrEmpty(Entry.CategoryName) || Entry.CategoryName == "Main Game"
            ? null : Entry.CategoryName.ToUpperInvariant();

        public FranchiseTimelineItem(FranchiseGameRef entry, bool isAbove)
        {
            Entry = entry;
            IsAbove = isAbove;
        }
    }
}
