using Codec.Models;
using Codec.Services.Storage;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
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
        private async Task SetLaunchScriptAsync(string batFilePath)
        {
            if (SelectedGame == null || string.IsNullOrWhiteSpace(batFilePath))
                return;

            SelectedGame.LaunchScript = batFilePath;
            OnPropertyChanged(nameof(SelectedGame));
            await _services.LibraryStorage.SaveAsync(Games.ToList());
        }

        [RelayCommand]
        private async Task ClearLaunchScriptAsync()
        {
            if (SelectedGame == null)
                return;

            SelectedGame.LaunchScript = null;
            OnPropertyChanged(nameof(SelectedGame));
            await _services.LibraryStorage.SaveAsync(Games.ToList());
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
            IsOnboardingVisible = Games.Count == 0;

            await _services.LibraryStorage.SaveAsync(Games.ToList());
        }

        [RelayCommand]
        private void PlayGame()
        {
            if (SelectedGame == null)
                return;

            try
            {
                bool isSteamGame = SelectedGame.SteamID.HasValue
                    && string.Equals(SelectedGame.ImportedFrom, "Steam", StringComparison.OrdinalIgnoreCase);

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
                else if (!string.IsNullOrWhiteSpace(SelectedGame.LaunchScript) && File.Exists(SelectedGame.LaunchScript))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = SelectedGame.LaunchScript,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(SelectedGame.LaunchScript)
                    });
                }
                else if (!string.IsNullOrWhiteSpace(SelectedGame.Executable) && File.Exists(SelectedGame.Executable))
                {
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
            _ = RefreshGameMetadataAsync(game);
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
                var steamTask = snapshot.SteamID.HasValue ? _services.SteamDetails.PopulateFromSteamAsync(snapshot) : Task.CompletedTask;
                var rawgTask = !snapshot.SteamID.HasValue && snapshot.RawgID.HasValue
                    ? _services.RawgDetails.PopulateAsync(snapshot)
                    : Task.CompletedTask;
                var folderSizeTask = FolderSizeService.CalculateAsync(snapshot.FolderLocation);

                await Task.WhenAll(steamTask, rawgTask, folderSizeTask);

                if (snapshot.SteamID.HasValue)
                {
                    if (!snapshot.IgdbId.HasValue)
                    {
                        snapshot.IgdbId = await _services.Igdb.FindIgdbIdBySteamIdAsync(snapshot.SteamID.Value);
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
            game.LibCapsuleCache = hydration.CapsuleCachePath;
            game.HasHeroAssetSource = hydration.HasHeroSource;
            game.LibHeroUrl = hydration.HeroUrl;
            game.LibHeroCache = hydration.HeroCachePath;
            game.HasLogoAssetSource = hydration.HasLogoSource;
            game.LibLogoUrl = hydration.LogoUrl;
            game.LibLogoCache = hydration.LogoCachePath;
        }
    }
}
