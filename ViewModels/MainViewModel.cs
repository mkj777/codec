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

namespace Codec.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private const int SidebarSearchDebounceDelayMs = 300;
        private static readonly StringComparer GameNameComparer = StringComparer.CurrentCultureIgnoreCase;

        private readonly DispatcherQueue _dispatcherQueue;
        private readonly ServiceHost _services;
        private readonly LibraryImportCoordinator _importCoordinator;
        private CancellationTokenSource? _sidebarSearchDebounceCts;
        private string _appliedSearchText = string.Empty;

        public ObservableCollection<Game> Games { get; set; } = new();
        public ObservableCollection<Game> SidebarFilteredGames { get; } = new();

        public MainViewModel(ServiceHost services)
        {
            _services = services;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _importCoordinator = new LibraryImportCoordinator(
                _services.GameImportPipeline,
                new GameScanner(_services.GameName),
                GetLibrarySnapshotAsync,
                CommitImportedGameAsync);
            _importCoordinator.StatusChanged += ImportCoordinator_StatusChanged;
            _importCoordinator.NotificationRaised += ImportCoordinator_NotificationRaised;
            Games.CollectionChanged += Games_CollectionChanged;
            RefreshSidebarFilteredGames();
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

        [ObservableProperty] private bool _isScanProgressVisible;
        [ObservableProperty] private string _scanProgressMessage = string.Empty;
        [ObservableProperty] private bool _scanProgressIsIndeterminate = true;
        [ObservableProperty] private double _scanProgressValue;
        [ObservableProperty] private double _scanProgressMaximum = 1;
        [ObservableProperty] private double _scanProgressMinimum;

        [ObservableProperty] private bool _isAppSpinnerActive;
        [ObservableProperty] private bool _isGameSettingsOpen;
        [ObservableProperty] private bool _isMediaOverlayOpen;
        [ObservableProperty] private bool _isUiEnabled = true;
        [ObservableProperty] private bool _isImportStatusVisible;
        [ObservableProperty] private string _importStatusMessage = string.Empty;
        [ObservableProperty] private int _queuedCount;
        [ObservableProperty] private int _processingCount;
        [ObservableProperty] private int _addedCount;
        [ObservableProperty] private int _skippedCount;
        [ObservableProperty] private int _failedCount;
        [ObservableProperty] private bool _isImportNotificationVisible;
        [ObservableProperty] private string _importNotificationTitle = "Library Import";
        [ObservableProperty] private string _importNotificationMessage = string.Empty;
        [ObservableProperty] private InfoBarSeverity _importNotificationBarSeverity = InfoBarSeverity.Informational;
        [ObservableProperty] private Game? _sidebarSelectedItem;
        [ObservableProperty] private string _searchText = string.Empty;

        [ObservableProperty]
        private bool _isDebugMode =
#if DEBUG
            true;
#else
            false;
#endif

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
        private void Back()
        {
            IsDetailsVisible = false;
            IsGameSettingsOpen = false;
            IsMediaOverlayOpen = false;
            SelectedGame = null;
            SidebarSelectedItem = null;
        }

        // ---------------------------------------------------------------------------------
        // Library Lifecycle
        // ---------------------------------------------------------------------------------

        public async Task LoadLibraryAsync()
        {
            _services.LibraryStorage.EnsureStorageInitialized();
            SetLoadingState(true, "Loading your library...", "Preparing Codec");

            var saved = await _services.LibraryStorage.LoadAsync();
            await EnsureCoversAsync(saved);
            var sortedSavedGames = saved
                .OrderBy(game => game.Name ?? string.Empty, GameNameComparer)
                .ThenBy(game => game.Id)
                .ToList();

            Games.Clear();
            foreach (var g in sortedSavedGames)
                Games.Add(g);

            await _services.LibraryStorage.SaveAsync(sortedSavedGames);
            QueueBackgroundPrefetch(Games);

            SetLoadingState(false);
            IsInitialLoading = false;
            IsOnboardingVisible = Games.Count == 0;
        }

        [RelayCommand]
        private async Task ScanGamesAsync()
        {
            IsOnboardingVisible = false;
            await _importCoordinator.StartScanAsync();
            IsOnboardingVisible = Games.Count == 0 && !IsImportStatusVisible;
        }

        public async Task<ImportEnqueueResult> AddGameCommand(string executablePath)
        {
            IsOnboardingVisible = false;
            var result = await _importCoordinator.EnqueueManualExecutableAsync(executablePath);
            await ShowImportNotificationAsync(
                "Library Import",
                result.Message,
                result.Status switch
                {
                    ImportEnqueueResultStatus.Accepted => Codec.Services.Importing.ImportNotificationSeverity.Informational,
                    ImportEnqueueResultStatus.Duplicate => Codec.Services.Importing.ImportNotificationSeverity.Warning,
                    ImportEnqueueResultStatus.Invalid => Codec.Services.Importing.ImportNotificationSeverity.Warning,
                    _ => Codec.Services.Importing.ImportNotificationSeverity.Error
                });
            if (!result.IsAccepted && Games.Count == 0)
            {
                IsOnboardingVisible = true;
            }
            return result;
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
        }

        private void RefreshSidebarFilteredGames()
        {
            var filteredGames = Games
                .Where(MatchesSidebarSearch)
                .OrderBy(game => game.Name ?? string.Empty, GameNameComparer)
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
        }

        private bool MatchesSidebarSearch(Game game)
        {
            if (string.IsNullOrWhiteSpace(_appliedSearchText))
                return true;

            return game.Name?.Contains(_appliedSearchText, StringComparison.OrdinalIgnoreCase) == true;
        }

        private void InsertGameAlphabetically(Game game)
        {
            int insertIndex = 0;

            while (insertIndex < Games.Count && CompareGamesByName(Games[insertIndex], game) <= 0)
                insertIndex++;

            Games.Insert(insertIndex, game);
        }

        private static int CompareGamesByName(Game left, Game right)
        {
            int nameComparison = GameNameComparer.Compare(left.Name ?? string.Empty, right.Name ?? string.Empty);
            if (nameComparison != 0)
                return nameComparison;

            return left.Id.CompareTo(right.Id);
        }

        private static string NormalizeSearchText(string? value) => value?.Trim() ?? string.Empty;
    }
}
