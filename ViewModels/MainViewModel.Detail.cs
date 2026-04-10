using Codec.Models;
using Codec.Services.Storage;
using CommunityToolkit.Mvvm.Input;
using System;
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
                if (!string.IsNullOrWhiteSpace(SelectedGame.LaunchScript) && File.Exists(SelectedGame.LaunchScript))
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
                var rawgTask = snapshot.RawgID.HasValue ? _services.RawgDetails.PopulateAsync(snapshot) : Task.CompletedTask;
                var steamTask = snapshot.SteamID.HasValue ? _services.SteamDetails.PopulateFromSteamAsync(snapshot) : Task.CompletedTask;
                var hltbTask = _services.Hltb.PopulateAsync(snapshot);
                var folderSizeTask = FolderSizeService.CalculateAsync(snapshot.FolderLocation);

                await Task.WhenAll(rawgTask, steamTask, hltbTask, folderSizeTask);
                var displayedAssets = await _services.DisplayedAssets.EnsureDisplayedAssetsAsync(snapshot);
                ApplyDisplayedAssetHydration(snapshot, displayedAssets);

                if (string.IsNullOrWhiteSpace(snapshot.RawgUrl))
                {
                    if (!string.IsNullOrWhiteSpace(snapshot.RawgSlug))
                        snapshot.RawgUrl = $"https://rawg.io/games/{snapshot.RawgSlug}";
                    else if (snapshot.RawgID.HasValue)
                        snapshot.RawgUrl = $"https://rawg.io/games/{snapshot.RawgID.Value}";
                    else
                        snapshot.RawgUrl = "https://rawg.io";
                }
                if (string.IsNullOrWhiteSpace(snapshot.HltbUrl))
                    snapshot.HltbUrl = "https://howlongtobeat.com";

                if (folderSizeTask.IsCompletedSuccessfully && snapshot.FolderSize != folderSizeTask.Result)
                    snapshot.FolderSize = folderSizeTask.Result;

                snapshot.IsFullyImported = displayedAssets.AreRequiredAssetsReady;

                var persistedSnapshot = await RunOnUiThreadAsync(() =>
                {
                    game.ApplyHydrationSnapshot(snapshot);
                    game.IsFullyImported = displayedAssets.AreRequiredAssetsReady;
                    game.NotifyDisplayedAssetStateChanged();
                    if (ReferenceEquals(SelectedGame, game))
                    {
                        OnPropertyChanged(nameof(SelectedGame));
                    }
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
            game.NotifyDisplayedAssetStateChanged();
        }
    }
}
