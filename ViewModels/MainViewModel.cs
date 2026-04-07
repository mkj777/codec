using Codec.Models;
using Codec.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Codec.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        public sealed record AddGameResult(bool IsAdded, string Message, Game? Game);

        // Settings Sidebar
        [RelayCommand]
        private void OpenGameSettings()
        {
            IsGameSettingsOpen = true;
        }

        [RelayCommand]
        private void CloseGameSettings()
        {
            IsGameSettingsOpen = false;
        }

        // Erhält den Pfad (z.B. der .bat Datei) von der View und speichert ihn
        [RelayCommand]
        private async Task SetLaunchScriptAsync(string batFilePath)
        {
            if (SelectedGame == null || string.IsNullOrWhiteSpace(batFilePath))
                return;

            SelectedGame.LaunchScript = batFilePath;

            // UI zwingen, den neuen Wert des SelectedGames neu zu laden
            OnPropertyChanged(nameof(SelectedGame));

            // Direkt persistent speichern, damit der Wert beim Neustart bleibt
            await LibraryStorageService.SaveAsync(Games);
        }

        [RelayCommand]
        private async Task ClearLaunchScriptAsync()
        {
            if (SelectedGame == null)
                return;

            SelectedGame.LaunchScript = null;
            OnPropertyChanged(nameof(SelectedGame));

            await LibraryStorageService.SaveAsync(Games);
        }

        [RelayCommand]
        private void PlayGame()
        {
            if (SelectedGame == null)
                return;

            try
            {
                // Check if there's a custom launch script
                if (!string.IsNullOrWhiteSpace(SelectedGame.LaunchScript) && File.Exists(SelectedGame.LaunchScript))
                {
                    // Launch using the custom script
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = SelectedGame.LaunchScript,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(SelectedGame.LaunchScript)
                    });
                }
                // Otherwise use the executable
                else if (!string.IsNullOrWhiteSpace(SelectedGame.Executable) && File.Exists(SelectedGame.Executable))
                {
                    // Launch the game executable
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = SelectedGame.Executable,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(SelectedGame.Executable)
                    });
                }
                else
                {
                    Debug.WriteLine($"Cannot launch {SelectedGame.Name}: executable not found at {SelectedGame.Executable}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to launch {SelectedGame.Name}: {ex.Message}");
            }
        }

        [RelayCommand]
        private void OpenGameFolder()
        {
            if (SelectedGame == null)
                return;

            try
            {
                string folderPath = SelectedGame.FolderLocation;

                if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
                {
                    // Open the folder in Windows Explorer
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{folderPath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Debug.WriteLine($"Cannot open folder for {SelectedGame.Name}: folder not found at {folderPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open folder for {SelectedGame.Name}: {ex.Message}");
            }
        }
        private readonly DispatcherQueue _dispatcherQueue;

        public ObservableCollection<Game> Games { get; set; } = new();

        public MainViewModel()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            Games.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasGames));
                OnPropertyChanged(nameof(IsEmptyLibrary));
                OnPropertyChanged(nameof(IsLibraryVisible));
            };
        }

        public bool HasGames => Games.Count > 0;
        public bool IsEmptyLibrary => !HasGames;

        // View switching
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLibraryGridVisible))]
        private bool _isDetailsVisible;

        public bool IsLibraryGridVisible => !IsDetailsVisible;

        // Loading / onboarding states
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

        // Scan progress overlay
        [ObservableProperty] private bool _isScanProgressVisible;
        [ObservableProperty] private string _scanProgressMessage = string.Empty;
        [ObservableProperty] private bool _scanProgressIsIndeterminate = true;
        [ObservableProperty] private double _scanProgressValue;
        [ObservableProperty] private double _scanProgressMaximum = 1;
        [ObservableProperty] private double _scanProgressMinimum;

        // Other overlays
        [ObservableProperty] private bool _isDetailLoadingVisible;
        [ObservableProperty] private bool _isAppSpinnerActive;

        // Settings sidebar
        [ObservableProperty] private bool _isGameSettingsOpen;

        // Sidebar lock
        [ObservableProperty] private bool _isUiEnabled = true;

        // Sidebar selection sync (BackCommand sets this to null to clear ListView)
        [ObservableProperty] private Game? _sidebarSelectedItem;

        // Debug mode detection
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
        // Commands
        // ---------------------------------------------------------------------------------

        [RelayCommand]
        private void Back()
        {
            IsDetailsVisible = false;
            IsGameSettingsOpen = false;
            SelectedGame = null;
            SidebarSelectedItem = null;
        }

        [RelayCommand]
        private async Task SelectGameAsync(Game game)
        {
            IsDetailLoadingVisible = true;

            var folderSizeTask = FolderSizeService.CalculateAsync(game.FolderLocation);

            try
            {
                var rawgTask = game.RawgID.HasValue ? RawgDetailsService.PopulateAsync(game) : Task.CompletedTask;
                var steamTask = game.SteamID.HasValue ? SteamDetailsService.PopulateFromSteamAsync(game) : Task.CompletedTask;
                var hltbTask = HltbService.PopulateAsync(game, _dispatcherQueue);

                await Task.WhenAll(rawgTask, steamTask);

                if (string.IsNullOrWhiteSpace(game.RawgUrl))
                {
                    string? rawgUrl = null;
                    if (!string.IsNullOrWhiteSpace(game.RawgSlug))
                        rawgUrl = $"https://rawg.io/games/{game.RawgSlug}";
                    else if (game.RawgID.HasValue)
                        rawgUrl = $"https://rawg.io/games/{game.RawgID.Value}";
                    game.RawgUrl = rawgUrl ?? "https://rawg.io";
                }
                if (string.IsNullOrWhiteSpace(game.HltbUrl))
                    game.HltbUrl = "https://howlongtobeat.com";

                SelectedGame = game;

                _ = folderSizeTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Debug.WriteLine($"Folder size fetch failed for {game.Name}: {t.Exception?.GetBaseException().Message}");
                        return;
                    }
                    var size = t.Result;
                    _ = _dispatcherQueue.TryEnqueue(() =>
                    {
                        if (game.FolderSize != size)
                        {
                            game.FolderSize = size;
                            _ = LibraryStorageService.SaveAsync(Games);
                        }
                    });
                }, TaskScheduler.Default);

                await AwaitHeroAndLogoAsync(game);

                IsDetailsVisible = true;

                _ = hltbTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Debug.WriteLine($"HLTB fetch failed: {t.Exception?.GetBaseException().Message}");
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            finally
            {
                IsDetailLoadingVisible = false;
            }
        }

        [RelayCommand]
        private async Task ScanGamesAsync()
        {
            IsUiEnabled = false;
            ShowScanProgress("Looking for Games (this might take a few minutes)...", isIndeterminate: true);

            try
            {
                var progress = new Progress<string>(_ => { });
                var scanner = new GameScanner();
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
                    .Select(g => RawgDetailsService.TryPopulateRawgFromSearchAsync(g));
                await Task.WhenAll(fallbackTasks);

                await PopulateGridDbDataAsync(newGames);

                int totalGames = newGames.Count;
                PrepareCoverProgress(totalGames);

                Games.Clear();
                int processed = 0;
                foreach (var g in newGames)
                {
                    await EnsureCoverForGameAsync(g);
                    Games.Add(g);
                    processed++;
                    UpdateCoverProgress(processed, totalGames);
                }

                await LibraryStorageService.SaveAsync(Games);
                QueueBackgroundPrefetch(Games);
            }
            finally
            {
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
            string detectedName = GameNameService.GetBestName(normalizedExePath) ?? Path.GetFileNameWithoutExtension(normalizedExePath);

            try
            {
                var (steamId, rawgId) = await GameNameService.FindGameIdsAsync(normalizedExePath);

                if (!rawgId.HasValue && !string.IsNullOrWhiteSpace(detectedName))
                {
                    var mode = steamId.HasValue ? RawgValidationMode.SteamBacked : RawgValidationMode.Strict;
                    rawgId = await GameDetailsService.ValidateGameAsync(detectedName, mode);
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
                    await SteamDetailsService.PopulateFromSteamAsync(game);
                }

                if (game.RawgID.HasValue)
                {
                    await RawgDetailsService.PopulateAsync(game);
                }
                else
                {
                    await RawgDetailsService.TryPopulateRawgFromSearchAsync(game);
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

                await HltbService.PopulateAsync(game, _dispatcherQueue);
                await EnsureCoverForGameAsync(game);

                Games.Add(game);
                await LibraryStorageService.SaveAsync(Games);
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

        public async Task LoadLibraryAsync()
        {
            LibraryStorageService.EnsureStorageInitialized();
            SetLoadingState(true, "Loading your library...", "Preparing Codec");

            var saved = await LibraryStorageService.LoadAsync();
            await EnsureCoversAsync(saved);

            Games.Clear();
            foreach (var g in saved)
                Games.Add(g);

            await LibraryStorageService.SaveAsync(Games);
            QueueBackgroundPrefetch(Games);

            SetLoadingState(false);
            IsInitialLoading = false;
            IsOnboardingVisible = Games.Count == 0;
        }

        public async Task RefreshCoversAsync()
        {
            ShowScanProgress("Fetching Covers...", Games.Count == 0);
            PrepareCoverProgress(Games.Count, "Update Cover", "No Games to update.");

            int processed = 0;
            foreach (var g in Games)
            {
                if (g.SteamID.HasValue)
                {
                    var cover = await GameAssetService.DownloadSteamLibraryCoverAsync(g.SteamID.Value, force: true);
                    if (!string.IsNullOrEmpty(cover))
                        g.LibCapsuleUrl = cover;
                    await Task.Delay(75);
                }
                else
                {
                    await GridDbService.TryPopulateGridAssetsAsync(g, forceCoverDownload: true);
                    await Task.Delay(75);
                }

                processed++;
                UpdateCoverProgress(processed, Games.Count, "Updating Cover");
            }

            await LibraryStorageService.SaveAsync(Games);
            HideScanProgress();
        }

        // ---------------------------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------------------------

        private async Task EnsureCoversAsync(IEnumerable<Game> games)
        {
            foreach (var g in games)
            {
                bool needsCover = IsPlaceholder(g.LibCapsule) || LocalFileMissing(g.LibCapsule);

                if (g.SteamID.HasValue && needsCover)
                {
                    try
                    {
                        Debug.WriteLine($"Fetching cover for {g.Name} (SteamID {g.SteamID})");
                        var cover = await GameAssetService.DownloadSteamLibraryCoverAsync(g.SteamID.Value);
                        if (!string.IsNullOrEmpty(cover))
                            g.LibCapsuleUrl = cover;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Cover fetch failed for {g.Name} ({g.SteamID}): {ex.Message}");
                    }
                    await Task.Delay(75);
                }
                else if (!g.SteamID.HasValue && needsCover)
                {
                    await GridDbService.TryPopulateGridAssetsAsync(g);
                    await Task.Delay(75);
                }
            }
        }

        private Task EnsureCoverForGameAsync(Game game) => EnsureCoversAsync(new[] { game });

        private static async Task PopulateGridDbDataAsync(IEnumerable<Game> games)
        {
            foreach (var game in games)
            {
                if (game.SteamID.HasValue)
                    continue;
                await GridDbService.TryPopulateGridAssetsAsync(game);
                await Task.Delay(75);
            }
        }

        private static bool IsPlaceholder(string? uri) =>
            string.IsNullOrWhiteSpace(uri) || uri.StartsWith("https://placehold.co/", StringComparison.OrdinalIgnoreCase);

        private static bool LocalFileMissing(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return true;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return true;
            if (parsed.IsFile)
            {
                try { return !File.Exists(parsed.LocalPath); } catch { return true; }
            }
            return false;
        }

        private async Task AwaitHeroAndLogoAsync(Game game)
        {
            bool NeedsLogo() => game.SteamID.HasValue;
            bool HasHero() => !IsPlaceholder(game.LibHero);
            bool HasLogo() => !IsPlaceholder(game.LibLogo);

            const int maxWaitMs = 6000;
            const int stepMs = 100;
            int waited = 0;

            while (waited < maxWaitMs)
            {
                if (HasHero() && (!NeedsLogo() || HasLogo()))
                    return;
                await Task.Delay(stepMs);
                waited += stepMs;
            }
        }

        private void ShowScanProgress(string message, bool isIndeterminate)
        {
            ScanProgressMessage = message;
            ScanProgressIsIndeterminate = isIndeterminate;
            ScanProgressValue = 0;
            ScanProgressMaximum = 1;
            IsScanProgressVisible = true;
        }

        private void PrepareCoverProgress(int totalGames, string? labelPrefix = null, string? emptyMessage = null)
        {
            if (totalGames <= 0)
            {
                ScanProgressIsIndeterminate = true;
                ScanProgressMessage = emptyMessage ?? "No new games found.";
                return;
            }
            ScanProgressIsIndeterminate = false;
            ScanProgressMinimum = 0;
            ScanProgressMaximum = totalGames;
            ScanProgressValue = 0;
            ScanProgressMessage = $"{labelPrefix ?? "Loading covers"} (0/{totalGames})";
        }

        private void UpdateCoverProgress(int processed, int total, string? labelPrefix = null)
        {
            if (total <= 0) return;
            ScanProgressIsIndeterminate = false;
            ScanProgressValue = Math.Min(processed, total);
            ScanProgressMessage = $"{labelPrefix ?? "Loading covers"} ({Math.Min(processed, total)}/{total})";
        }

        private void HideScanProgress() => IsScanProgressVisible = false;

        private void QueueBackgroundPrefetch(IEnumerable<Game> games)
        {
            foreach (var game in games)
            {
                QueueSteamWarmups(game);
                QueueRawgWarmups(game);
                QueueHltbWarmups(game);
            }
        }

        private static void QueueSteamWarmups(Game game)
        {
            if (!game.SteamID.HasValue) return;
            int id = game.SteamID.Value;
            DataCacheService.QueueWarmup($"https://store.steampowered.com/api/appdetails?appids={id}");
            DataCacheService.QueueWarmup($"https://store.steampowered.com/appreviews/{id}/?json=1&language=all&filter=all&num_per_page=0");
            DataCacheService.QueueWarmup($"https://steamspy.com/api.php?request=appdetails&appid={id}");
        }

        private static void QueueRawgWarmups(Game game)
        {
            if (game.RawgID.HasValue)
            {
                DataCacheService.QueueWarmup($"https://codec-api-proxy.vercel.app/api/rawg/details?id={game.RawgID.Value}");
                return;
            }
            if (!string.IsNullOrWhiteSpace(game.Name))
            {
                string term = Uri.EscapeDataString(game.Name);
                DataCacheService.QueueWarmup($"https://codec-api-proxy.vercel.app/api/rawg/search?term={term}");
            }
        }

        private static void QueueHltbWarmups(Game game)
        {
            if (string.IsNullOrWhiteSpace(game.Name)) return;
            string normalized = NormalizeForHltb(game.Name);
            if (!string.IsNullOrWhiteSpace(normalized))
                DataCacheService.QueueWarmup($"https://codec-api-proxy.vercel.app/api/hltb/search?term={Uri.EscapeDataString(normalized)}");
        }

        private static string NormalizeForHltb(string value)
        {
            string cleaned = Regex.Replace(value, "[^a-zA-Z0-9 ]", " ");
            cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
            return cleaned;
        }
    }
}
