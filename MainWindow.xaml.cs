using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Codec
{
    public sealed partial class MainWindow : Window
    {
        // ObservableCollection to hold the list of games, automatically updates the UI when modified
        public ObservableCollection<Game> Games { get; set; }

        // defines the path to the JSON file where the game library is saved
        private readonly string _savePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "codec_library.json");

        // MainWindow constructor
        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "Codec Game Library";
            Games = new ObservableCollection<Game>();
            ExtendsContentIntoTitleBar = true;
            LoadGamesAsync();
        }

        private async void AddGame_Click(object sender, RoutedEventArgs e)
        {
            var exePicker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            exePicker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(exePicker, WindowNative.GetWindowHandle(this));

            StorageFile exeFile = await exePicker.PickSingleFileAsync();
            if (exeFile == null) return; // User cancelled the picker

            // TEST: Call GameNameService
            Debug.WriteLine($"Starting ID lookup for: {exeFile.Path}");
            var (steamId, rawgId) = await GameNameService.FindGameIdsAsync(exeFile.Path);

            // Show test popup with results
            string steamIdText = steamId.HasValue ? steamId.Value.ToString() : "Not found";
            string rawgIdText = rawgId.HasValue ? rawgId.Value.ToString() : "Not found";
            string bestName = GameNameService.GetBestName(exeFile.Path);

            var testDialog = new ContentDialog
            {
                Title = "Game ID Lookup Results",
                Content = $"Best Name: {bestName}\n\n" +
                          $"Steam ID: {steamIdText}\n" +
                          $"RAWG ID: {rawgIdText}",
                CloseButtonText = "Ok",
                XamlRoot = this.Content.XamlRoot
            };

            await testDialog.ShowAsync();

            // REMOVED: Script prompt and adding to grid for testing
            // The code below is commented out for now

            /*
            // optional Start Script Prompt
            string? scriptPath = null;
            var dialog = new ContentDialog
            {
                Title = "Add a Launch Script?",
                Content = "Some games may require a Launch Script (.bat or .ps1)",
                PrimaryButtonText = "Yes",
                CloseButtonText = "No",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                var scriptPicker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                scriptPicker.FileTypeFilter.Add(".bat");
                scriptPicker.FileTypeFilter.Add(".ps1");
                InitializeWithWindow.Initialize(scriptPicker, WindowNative.GetWindowHandle(this));
                StorageFile scriptFile = await scriptPicker.PickSingleFileAsync();
                if (scriptFile != null)
                {
                    scriptPath = scriptFile.Path;
                }
            }

            // creates a Game-Object
            string gameName = Path.GetFileNameWithoutExtension(exeFile.Name);
            string coverPath = await ExtractAndSaveIconAsync(exeFile.Path, gameName);

            var newGame = new Game
            {
                Name = gameName,
                Executable = exeFile.Path,
                Cover = coverPath,
                LaunchScript = scriptPath,
                SteamID = steamId,
                RawgID = rawgId
            };

            Games.Add(newGame);
            await SaveGamesAsync();
            */
        }


        // triggered when play button is clicked
        private async void PlayGame_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Game gameToPlay)
            {

                await SaveGamesAsync();

                try
                {
                    // start the game via LaunchScript or directly via the Executable
                    if (!string.IsNullOrEmpty(gameToPlay.LaunchScript))
                    {
                        string extension = Path.GetExtension(gameToPlay.LaunchScript).ToLower();
                        string scriptDirectory = Path.GetDirectoryName(gameToPlay.LaunchScript);

                        if (extension == ".ps1")
                        {
                            // execute PowerShell script
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "powershell.exe",
                                Arguments = $"-ExecutionPolicy Bypass -File \"{gameToPlay.LaunchScript}\"",
                                UseShellExecute = true,
                                WorkingDirectory = scriptDirectory
                            });
                        }
                        else // .bat or other script types
                        {
                            Process.Start(new ProcessStartInfo(gameToPlay.LaunchScript)
                            {
                                UseShellExecute = true,
                                WorkingDirectory = scriptDirectory
                            });
                        }
                    }
                    else
                    {
                        // .exe directly if no LaunchScript was provided
                        Process.Start(new ProcessStartInfo(gameToPlay.Executable)
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Path.GetDirectoryName(gameToPlay.Executable)
                        });
                    }
                }
                catch (Exception ex)
                {
                    string path = !string.IsNullOrEmpty(gameToPlay.LaunchScript) ? gameToPlay.LaunchScript : gameToPlay.Executable;
                    ShowErrorDialog("Error while trying to run the Game", $"The Path '{path}' could not be resolved\n\nError Message: {ex.Message}");
                }
            }
        }

        // triggered when the remove button is clicked
        private async void RemoveGame_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Game gameToRemove)
            {
                Games.Remove(gameToRemove);
                await SaveGamesAsync(); // save the library after removing a game
            }
        }


        // Data Logic
        // saves the entire 'Games' collection to the JSON file.
        private async Task SaveGamesAsync()
        {
            try
            {
                var jsonString = JsonSerializer.Serialize(Games);
                await File.WriteAllTextAsync(_savePath, jsonString);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error while saving your Library {ex.Message}");
            }
        }

        // loads the game library from the JSON file.
        private async void LoadGamesAsync()
        {
            if (File.Exists(_savePath))
            {
                try
                {
                    var jsonString = await File.ReadAllTextAsync(_savePath);
                    var loadedGames = JsonSerializer.Deserialize<ObservableCollection<Game>>(jsonString);
                    if (loadedGames != null)
                    {
                        // Clear the existing list and add the loaded games.
                        Games.Clear();
                        foreach (var game in loadedGames)
                        {
                            Games.Add(game);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error while loading your Library {ex.Message}");
                    // optional: show an error dialog to the user if the library is corrupt.
                }
            }
        }

        // Utility Methods
        // extracts the icon from the executable and saves it as a PNG in the local app data folder
        private async Task<string> ExtractAndSaveIconAsync(string exePath, string gameName)
        {
            // creates a "Covers" folder in the local app data directory
            var coversFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("Covers", CreationCollisionOption.OpenIfExists);
            string iconPath = Path.Combine(coversFolder.Path, $"{gameName}.png");

            try
            {
                // uses System.Drawing to extract the icon from the executable
                using (Icon? ico = Icon.ExtractAssociatedIcon(exePath))
                {
                    if (ico != null)
                    {
                        // converts the icon to a Bitmap and saves it as PNG
                        using (var bmp = ico.ToBitmap())
                        {
                            bmp.Save(iconPath, System.Drawing.Imaging.ImageFormat.Png);
                            return iconPath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Couldnt extract Icon {exePath}. Error Message: {ex.Message}");
            }

            // fallback to a placeholder image if icon extraction fails
            return "https://placehold.co/180x240/1c1c1c/ffffff?text=Kein+Cover";
        }


        // helper method to show error dialogs
        private async void ShowErrorDialog(string title, string content)
        {
            var errorDialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "Ok",
                XamlRoot = this.Content.XamlRoot // ensures the dialog is properly parented
            };
            await errorDialog.ShowAsync();
        }
    }
}

