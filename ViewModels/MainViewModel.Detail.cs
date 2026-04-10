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
            await _services.LibraryStorage.SaveAsync(Games);
        }

        [RelayCommand]
        private async Task ClearLaunchScriptAsync()
        {
            if (SelectedGame == null)
                return;

            SelectedGame.LaunchScript = null;
            OnPropertyChanged(nameof(SelectedGame));
            await _services.LibraryStorage.SaveAsync(Games);
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

            await _services.LibraryStorage.SaveAsync(Games);
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
            try
            {
                var rawgTask = game.RawgID.HasValue ? _services.RawgDetails.PopulateAsync(game) : Task.CompletedTask;
                var steamTask = game.SteamID.HasValue ? _services.SteamDetails.PopulateFromSteamAsync(game) : Task.CompletedTask;
                var hltbTask = _services.Hltb.PopulateAsync(game, _dispatcherQueue);
                var folderSizeTask = FolderSizeService.CalculateAsync(game.FolderLocation);

                await Task.WhenAll(rawgTask, steamTask, hltbTask, folderSizeTask);

                if (string.IsNullOrWhiteSpace(game.RawgUrl))
                {
                    if (!string.IsNullOrWhiteSpace(game.RawgSlug))
                        game.RawgUrl = $"https://rawg.io/games/{game.RawgSlug}";
                    else if (game.RawgID.HasValue)
                        game.RawgUrl = $"https://rawg.io/games/{game.RawgID.Value}";
                    else
                        game.RawgUrl = "https://rawg.io";
                }
                if (string.IsNullOrWhiteSpace(game.HltbUrl))
                    game.HltbUrl = "https://howlongtobeat.com";

                if (folderSizeTask.IsCompletedSuccessfully && game.FolderSize != folderSizeTask.Result)
                    game.FolderSize = folderSizeTask.Result;

                _ = _dispatcherQueue.TryEnqueue(() => _ = _services.LibraryStorage.SaveAsync(Games));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Background metadata refresh failed for {game.Name}: {ex.Message}");
            }
        }
    }
}
