using Codec.Models;
using Codec.Services;
using Codec.Services.Resolving;
using Codec.Services.Scanning;
using Codec.Services.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
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
        public sealed record AddGameResult(bool IsAdded, string Message, Game? Game);
        private const int SidebarSearchDebounceDelayMs = 300;
        private static readonly StringComparer GameNameComparer = StringComparer.CurrentCultureIgnoreCase;

        private readonly DispatcherQueue _dispatcherQueue;
        private readonly ServiceHost _services;
        private CancellationTokenSource? _sidebarSearchDebounceCts;
        private string _appliedSearchText = string.Empty;

        public ObservableCollection<Game> Games { get; set; } = new();
        public ObservableCollection<Game> SidebarFilteredGames { get; } = new();

        public MainViewModel(ServiceHost services)
        {
            _services = services;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
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

            await _services.LibraryStorage.SaveAsync(Games);
            QueueBackgroundPrefetch(Games);

            SetLoadingState(false);
            IsInitialLoading = false;
            IsOnboardingVisible = Games.Count == 0;
        }

        [RelayCommand]
        private async Task ScanGamesAsync()
        {
            IsUiEnabled = false;
            IsOnboardingVisible = false;
            HideScanProgress();
            SetLoadingState(true, "Finding your games...", "This will take a few minutes...");

            try
            {
                var progress = new Progress<string>(message =>
                {
                    if (!string.IsNullOrWhiteSpace(message))
                        LoadingSubtitle = message;
                });
                var scanner = new GameScanner(_services.GameName);
                var scanResults = await scanner.ScanAllGamesAsync(progress);

                var newGames = new List<Game>();
                var excludedGameNames = new[] { "Steamworks Common Redistributables", "Steam Linux Runtime", "Proton", "Steam Audio", "Steam VR" };

                foreach (var (steamAppId, gameName, rawgId, importSource, executablePath, folderLocation) in scanResults)
                {
                    try
                    {
                        if (excludedGameNames.Any(excl => gameName.Contains(excl, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        newGames.Add(new Game
                        {
                            Name = gameName,
                            Executable = executablePath,
                            FolderLocation = folderLocation,
                            ImportedFrom = importSource,
                            SteamID = steamAppId,
                            RawgID = rawgId
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error processing {gameName}: {ex.Message}");
                    }
                }

                var fallbackTasks = newGames
                    .Where(g => !g.RawgID.HasValue)
                    .Select(g => _services.RawgDetails.TryPopulateRawgFromSearchAsync(g));
                await Task.WhenAll(fallbackTasks);

                await PopulateGridDbDataAsync(newGames);
                newGames = newGames
                    .OrderBy(game => game.Name ?? string.Empty, GameNameComparer)
                    .ThenBy(game => game.Id)
                    .ToList();

                int totalGames = newGames.Count;
                LoadingTitle = totalGames > 0 ? "Loading covers..." : "Wrapping things up...";
                LoadingSubtitle = totalGames > 0
                    ? $"Preparing artwork for {totalGames} game{(totalGames == 1 ? string.Empty : "s")}..."
                    : "No new games found.";

                Games.Clear();
                int processed = 0;
                foreach (var g in newGames)
                {
                    await EnsureCoverForGameAsync(g);
                    Games.Add(g);
                    processed++;
                    if (totalGames > 0)
                        LoadingSubtitle = $"Preparing artwork... ({processed}/{totalGames})";
                }

                await _services.LibraryStorage.SaveAsync(Games);
                QueueBackgroundPrefetch(Games);
            }
            finally
            {
                SetLoadingState(false);
                HideScanProgress();
                IsUiEnabled = true;
                IsOnboardingVisible = Games.Count == 0;
            }
        }

        public async Task<AddGameResult> AddGameCommand(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return new AddGameResult(false, "No executable was selected.", null);
            }

            string normalizedExePath;
            try
            {
                normalizedExePath = Path.GetFullPath(executablePath);
            }
            catch
            {
                return new AddGameResult(false, "The selected executable path is invalid.", null);
            }

            if (!File.Exists(normalizedExePath))
            {
                return new AddGameResult(false, "The selected executable no longer exists.", null);
            }

            if (Games.Any(g => string.Equals(g.Executable, normalizedExePath, StringComparison.OrdinalIgnoreCase)))
            {
                return new AddGameResult(false, "This executable is already in your library.", null);
            }

            string folderLocation = Path.GetDirectoryName(normalizedExePath) ?? string.Empty;
            string detectedName = _services.GameName.GetBestName(normalizedExePath) ?? Path.GetFileNameWithoutExtension(normalizedExePath);

            try
            {
                var (steamId, rawgId) = await _services.GameName.FindGameIdsAsync(normalizedExePath);

                if (!rawgId.HasValue && !string.IsNullOrWhiteSpace(detectedName))
                {
                    var mode = steamId.HasValue ? RawgValidationMode.SteamBacked : RawgValidationMode.Strict;
                    rawgId = await _services.GameDetails.ValidateGameAsync(detectedName, mode);
                }

                if (steamId.HasValue && Games.Any(g => g.SteamID == steamId.Value))
                {
                    return new AddGameResult(false, $"A game with Steam ID {steamId.Value} already exists in your library.", null);
                }

                if (rawgId.HasValue && Games.Any(g => g.RawgID == rawgId.Value))
                {
                    return new AddGameResult(false, $"A game with RAWG ID {rawgId.Value} already exists in your library.", null);
                }

                var game = new Game
                {
                    Name = string.IsNullOrWhiteSpace(detectedName) ? Path.GetFileNameWithoutExtension(normalizedExePath) : detectedName,
                    Executable = normalizedExePath,
                    FolderLocation = folderLocation,
                    ImportedFrom = "Manual Executable",
                    SteamID = steamId,
                    RawgID = rawgId
                };

                if (Directory.Exists(folderLocation))
                {
                    try
                    {
                        game.FolderSize = await FolderSizeService.CalculateAsync(folderLocation);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Folder size lookup failed for {game.Name}: {ex.Message}");
                    }
                }

                if (game.SteamID.HasValue)
                {
                    await _services.SteamDetails.PopulateFromSteamAsync(game);
                }

                if (game.RawgID.HasValue)
                {
                    await _services.RawgDetails.PopulateAsync(game);
                }
                else
                {
                    await _services.RawgDetails.TryPopulateRawgFromSearchAsync(game);
                }

                if (string.IsNullOrWhiteSpace(game.RawgUrl))
                {
                    if (!string.IsNullOrWhiteSpace(game.RawgSlug))
                    {
                        game.RawgUrl = $"https://rawg.io/games/{game.RawgSlug}";
                    }
                    else if (game.RawgID.HasValue)
                    {
                        game.RawgUrl = $"https://rawg.io/games/{game.RawgID.Value}";
                    }
                    else
                    {
                        game.RawgUrl = "https://rawg.io";
                    }
                }

                if (string.IsNullOrWhiteSpace(game.HltbUrl))
                {
                    game.HltbUrl = "https://howlongtobeat.com";
                }

                await _services.Hltb.PopulateAsync(game, _dispatcherQueue);
                await EnsureCoverForGameAsync(game);

                InsertGameAlphabetically(game);
                await _services.LibraryStorage.SaveAsync(Games);
                QueueBackgroundPrefetch(new[] { game });

                IsOnboardingVisible = false;

                return new AddGameResult(true, $"{game.Name} was added to your library.", game);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Add by executable failed for '{normalizedExePath}': {ex.Message}");
                return new AddGameResult(false, "Codec could not add this executable. Use Debug > Check IDs for diagnostics.", null);
            }
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
