using Codec.Models;
using Codec.Services.Storage;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Codec.ViewModels
{
    public partial class MainViewModel
    {
        // ---------------------------------------------------------------------------------
        // Game Detail Commands
        // ---------------------------------------------------------------------------------

        [RelayCommand]
        private void OpenGameSettings()
        {
            IsGameSettingsOpen = true;
        }

        [RelayCommand]
        private void ToggleGameSettings()
        {
            IsGameSettingsOpen = !IsGameSettingsOpen;
        }

        [RelayCommand]
        private void CloseGameSettings()
        {
            IsGameSettingsOpen = false;
        }

        [RelayCommand]
        private void OpenMediaOverlay()
        {
            IsMediaOverlayOpen = true;
        }

        [RelayCommand]
        private void CloseMediaOverlay()
        {
            IsMediaOverlayOpen = false;
        }

        // Mainline = main game (0) only
        private const int MainlineFranchiseCategory = 0;
        // Always exclude: bundle (3), mod (5)
        private static readonly HashSet<int> ExcludedFranchiseCategories = new() { 3, 5 };
        private static readonly HashSet<string> LaunchAvailabilityPropertyNames = new(StringComparer.Ordinal)
        {
            nameof(Game.Executable),
            nameof(Game.ImportedFrom),
            nameof(Game.SteamID),
            nameof(Game.EpicAppId),
            nameof(Game.LaunchScript),
            nameof(Game.UseLaunchScriptOverride),
            nameof(Game.UseExecutableOverride),
            nameof(Game.CanLaunch)
        };

        public bool CanPlaySelectedGame => SelectedGame?.CanLaunch == true;

        public string PlayButtonText => CanPlaySelectedGame ? "PLAY" : "MISSING";

        public double PlayButtonOpacity => CanPlaySelectedGame ? 1d : 0.46d;

        public string PlayButtonToolTip => CanPlaySelectedGame
            ? "Launch game"
            : "Missing launch target";

        partial void OnSelectedGameChanged(Game? oldValue, Game? newValue)
        {
            if (oldValue != null)
            {
                oldValue.PropertyChanged -= SelectedGame_PropertyChanged;
            }

            if (newValue != null)
            {
                newValue.PropertyChanged += SelectedGame_PropertyChanged;
            }

            NotifyPlayAvailabilityChanged();
        }

        private void SelectedGame_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == null || LaunchAvailabilityPropertyNames.Contains(e.PropertyName))
            {
                NotifyPlayAvailabilityChanged();
            }
        }

        private void NotifyPlayAvailabilityChanged()
        {
            OnPropertyChanged(nameof(CanPlaySelectedGame));
            OnPropertyChanged(nameof(PlayButtonText));
            OnPropertyChanged(nameof(PlayButtonOpacity));
            OnPropertyChanged(nameof(PlayButtonToolTip));
            PlayGameCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand]
        private void OpenFranchiseOverlay()
        {
            if (SelectedGame?.FranchiseGames == null) return;
            FranchiseFilterMode = 0;
            RebuildFranchiseTimelineItems();
            IsFranchiseOverlayOpen = true;
        }

        [RelayCommand]
        private void CloseFranchiseOverlay() => IsFranchiseOverlayOpen = false;

        // Extended mode: mainline + DLC + expansion + standalone expansion + expanded game + remake + remaster
        private static readonly HashSet<int> ExtendedFranchiseCategories = new() { 0, 1, 2, 4, 8, 9, 10 };

        partial void OnFranchiseFilterModeChanged(int value) => RebuildFranchiseTimelineItems();

        private void RebuildFranchiseTimelineItems()
        {
            if (SelectedGame?.FranchiseGames == null)
            {
                FranchiseTimelineItems = null;
                FranchiseMainlineCount = 0;
                FranchiseExtendedCount = 0;
                FranchiseAllCount = 0;
                return;
            }

            var base_ = SelectedGame.FranchiseGames
                .Where(g => g.ReleaseDate.HasValue)
                .Where(g => !g.IgdbCategory.HasValue || !ExcludedFranchiseCategories.Contains(g.IgdbCategory.Value))
                .ToList();

            FranchiseMainlineCount = base_.Count(g => g.IgdbCategory == MainlineFranchiseCategory);
            FranchiseExtendedCount = base_.Count(g => !g.IgdbCategory.HasValue || ExtendedFranchiseCategories.Contains(g.IgdbCategory.Value));
            FranchiseAllCount = base_.Count;

            IEnumerable<FranchiseGameRef> filtered = FranchiseFilterMode switch
            {
                0 => base_.Where(g => g.IgdbCategory == MainlineFranchiseCategory),
                1 => base_.Where(g => !g.IgdbCategory.HasValue || ExtendedFranchiseCategories.Contains(g.IgdbCategory.Value)),
                _ => base_
            };

            FranchiseTimelineItems = filtered
                .OrderBy(g => g.ReleaseDate)
                .Select((e, i) => new FranchiseTimelineItem(e, i % 2 == 0))
                .ToList();
        }

        [RelayCommand]
        private async Task SetLaunchScriptAsync(string launchScriptPath)
        {
            if (SelectedGame == null)
                return;

            string? normalizedPath = NormalizeExistingFilePath(launchScriptPath);
            if (normalizedPath == null)
                return;

            SelectedGame.LaunchScript = normalizedPath;
            SelectedGame.UseLaunchScriptOverride = true;
            SelectedGame.UseExecutableOverride = false;
            OnPropertyChanged(nameof(SelectedGame));
            await _services.LibraryStorage.SaveAsync(Games.ToList());
        }

        [RelayCommand]
        private async Task SetExecutableAsync(string executablePath)
        {
            if (SelectedGame == null)
                return;

            string? normalizedPath = NormalizeExistingFilePath(executablePath);
            if (normalizedPath == null)
                return;

            SelectedGame.Executable = normalizedPath;
            SelectedGame.FolderLocation = Path.GetDirectoryName(normalizedPath) ?? SelectedGame.FolderLocation;
            SelectedGame.LaunchScript = null;
            SelectedGame.UseLaunchScriptOverride = false;
            SelectedGame.UseExecutableOverride = SelectedGame.IsSteamLaunchTarget
                || SelectedGame.IsEpicLaunchTarget
                || SelectedGame.IsRiotLaunchTarget;
            OnPropertyChanged(nameof(SelectedGame));
            await _services.LibraryStorage.SaveAsync(Games.ToList());
        }

        [RelayCommand]
        private async Task ResetLaunchOptionsAsync()
        {
            if (SelectedGame == null)
                return;

            SelectedGame.LaunchScript = null;
            SelectedGame.UseLaunchScriptOverride = false;
            SelectedGame.UseExecutableOverride = false;
            OnPropertyChanged(nameof(SelectedGame));
            await _services.LibraryStorage.SaveAsync(Games.ToList());
        }

        private static string? NormalizeExistingFilePath(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            try
            {
                string normalizedPath = Path.GetFullPath(filePath);
                return File.Exists(normalizedPath) ? normalizedPath : null;
            }
            catch
            {
                return null;
            }
        }

        [RelayCommand]
        private async Task DeleteSelectedGameAsync()
        {
            if (SelectedGame == null)
                return;

            var gameToDelete = SelectedGame;
            var removed = Games.Remove(gameToDelete);

            if (!removed)
            {
                var matchingGame = Games.FirstOrDefault(game => game.Id == gameToDelete.Id);
                if (matchingGame != null)
                    removed = Games.Remove(matchingGame);
            }

            if (!removed)
                return;

            IsGameSettingsOpen = false;
            IsDetailsVisible = false;
            SelectedGame = null;
            SidebarSelectedItem = null;
            IsOnboardingVisible = Games.Count == 0 && !_appSettings.OnboardingCompleted;

            await _services.LibraryStorage.SaveAsync(Games.ToList());
        }

        [RelayCommand(CanExecute = nameof(CanPlaySelectedGame))]
        private void PlayGame()
        {
            if (SelectedGame == null || !SelectedGame.CanLaunch)
                return;

            try
            {
                bool isSteamGame = SelectedGame.IsSteamLaunchTarget;
                bool isEpicGame = SelectedGame.IsEpicLaunchTarget;
                bool isRiotGame = SelectedGame.IsRiotLaunchTarget;

                if (SelectedGame.HasCustomLaunchScript && TryLaunchFile(SelectedGame.LaunchScript))
                {
                    return;
                }

                if (SelectedGame.UseExecutableOverride && TryLaunchFile(SelectedGame.Executable))
                {
                    return;
                }

                if (isSteamGame)
                {
                    bool steamRunning = Process.GetProcessesByName("steam").Length > 0;
                    if (!steamRunning)
                        TryLaunchSteamSilent();

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"steam://rungameid/{SelectedGame.SteamID!.Value}",
                        UseShellExecute = true
                    });
                }
                else if (isEpicGame)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = BuildEpicLaunchUri(SelectedGame.EpicAppId!),
                        UseShellExecute = true
                    });
                }
                else if (isRiotGame && TryLaunchFile(SelectedGame.LaunchScript))
                {
                    return;
                }
                else if (!TryLaunchFile(SelectedGame.Executable))
                {
                    Debug.WriteLine($"Cannot launch {SelectedGame.Name}: executable not found at {SelectedGame.Executable}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to launch {SelectedGame.Name}: {ex.Message}");
            }
        }

        private static bool TryLaunchFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(filePath)
            });

            return true;
        }

        internal static string BuildEpicLaunchUri(string epicAppId) =>
            $"com.epicgames.launcher://apps/{Uri.EscapeDataString(epicAppId)}?action=launch&silent=true";

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

        // ---------------------------------------------------------------------------------
        // Game Selection (offline-first: show immediately, refresh in background)
        // ---------------------------------------------------------------------------------

        [RelayCommand]
        private void SelectGame(Game game)
        {
            SelectedGame = game;
            IsDetailsVisible = true;
        }

        private async Task RefreshGameMetadataAsync(Game game)
        {
            if (game.IsFullyImported && game.DisplayedAssetsReady)
            {
                return;
            }

            try
            {
                var snapshot = game.CreateHydrationSnapshot();
                if (snapshot.SteamID.HasValue && snapshot.UsesAlternateMetadataLookupName && !snapshot.SteamMetadataAppId.HasValue)
                {
                    var resolvedBaseSteam = await _services.GameName.FindGameIdsByNameAsync(snapshot.EffectiveMetadataLookupName);
                    if (resolvedBaseSteam.steamId.HasValue && resolvedBaseSteam.steamId.Value != snapshot.SteamID.Value)
                    {
                        snapshot.SteamMetadataAppId = resolvedBaseSteam.steamId.Value;
                        if (!string.IsNullOrWhiteSpace(resolvedBaseSteam.steamName))
                        {
                            snapshot.MetadataLookupName = resolvedBaseSteam.steamName;
                        }
                    }
                }

                int? steamMetadataId = snapshot.EffectiveSteamMetadataAppId;
                var steamTask = steamMetadataId.HasValue ? _services.SteamDetails.PopulateFromSteamAsync(snapshot) : Task.CompletedTask;
                var rawgTask = !steamMetadataId.HasValue && snapshot.RawgID.HasValue
                    ? _services.RawgDetails.PopulateAsync(snapshot)
                    : Task.CompletedTask;
                var folderSizeTask = FolderSizeService.CalculateAsync(snapshot.FolderLocation);

                await Task.WhenAll(steamTask, rawgTask, folderSizeTask);

                if (steamMetadataId.HasValue)
                {
                    if (!snapshot.IgdbId.HasValue)
                    {
                        snapshot.IgdbId = await _services.Igdb.FindIgdbIdBySteamIdAsync(steamMetadataId.Value);
                    }
                    if (snapshot.IgdbId.HasValue)
                    {
                        await _services.Igdb.PopulateFromIgdbAsync(snapshot);
                    }
                }

                var displayedAssets = await _services.DisplayedAssets.EnsureDisplayedAssetsAsync(snapshot);
                ApplyDisplayedAssetHydration(snapshot, displayedAssets);

                if (snapshot.RawgID.HasValue && string.IsNullOrWhiteSpace(snapshot.RawgUrl))
                {
                    snapshot.RawgUrl = !string.IsNullOrWhiteSpace(snapshot.RawgSlug)
                        ? $"https://rawg.io/games/{snapshot.RawgSlug}"
                        : $"https://rawg.io/games/{snapshot.RawgID.Value}";
                }

                if (folderSizeTask.IsCompletedSuccessfully && snapshot.FolderSize != folderSizeTask.Result)
                    snapshot.FolderSize = folderSizeTask.Result;

                snapshot.IsFullyImported = displayedAssets.AreRequiredAssetsReady;

                var persistedSnapshot = await RunOnUiThreadAsync(() =>
                {
                    game.ApplyHydrationSnapshot(snapshot);
                    return Games.ToList();
                });

                await _services.LibraryStorage.SaveAsync(persistedSnapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Background metadata refresh failed for {game.Name}: {ex.Message}");
            }
        }

        private static void ApplyDisplayedAssetHydration(Game game, Services.Fetching.DisplayedAssetService.DisplayedAssetHydrationResult hydration)
        {
            game.GridDbId = hydration.GridDbId ?? game.GridDbId;
            game.LibraryCapsuleCache = hydration.CapsuleCachePath;
            game.HasHeroAssetSource = hydration.HasHeroSource;
            game.LibraryHeroUrl = hydration.HeroUrl;
            game.LibraryHeroCache = hydration.HeroCachePath;
            game.HasLogoAssetSource = hydration.HasLogoSource;
            game.LibraryLogoUrl = hydration.LogoUrl;
            game.LibraryLogoCache = hydration.LogoCachePath;
        }
    }
}
