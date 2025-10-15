using Codec.Models;
using Codec.Services;
using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Linq;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Codec
{
    public sealed partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "Codec Game Library";
            ViewModel = new MainViewModel();
            ExtendsContentIntoTitleBar = true;
        }

        private async void AddGame_Click(object sender, RoutedEventArgs e)
        {
            var exePicker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            exePicker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(exePicker, WindowNative.GetWindowHandle(this));

            var exeFile = await exePicker.PickSingleFileAsync();
            if (exeFile == null) return; // User cancelled the picker

            // Call GameNameService
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
        }

        private async void ScanGames_Click(object sender, RoutedEventArgs e)
        {
            // Disable button during scan
            var button = sender as Button;
            if (button != null)
            {
                button.IsEnabled = false;
                button.Content = "Scanning...";
            }

            try
            {
                // Progress handler for UI updates
                var progress = new Progress<string>(status =>
                {
                    Debug.WriteLine(status);
                    if (button != null)
                    {
                        button.Content = status;
                    }
                });

                // Clear previous results
                ViewModel.Games.Clear();

                // Start Steam library scan
                var scanResults = await GameScanner.ScanSteamLibraryAsync(progress);

                Debug.WriteLine($"Scan complete. Processing {scanResults.Count} games...");

                // Games to exclude from display (but still scan)
                var excludedGameNames = new[]
                {
                    "Steamworks Common Redistributables",
                    "Steam Linux Runtime",
                    "Proton",
                    "Steam Audio",
                    "Steam VR"
                };

                // Process each found game
                int totalScanned = 0;
                int excluded = 0;
                int gamesWithExecutable = 0;

                foreach (var (steamAppId, gameName, rawgId, importSource, executablePath, folderLocation) in scanResults)
                {
                    try
                    {
                        totalScanned++;
                        
                        // Check if this game should be excluded from display
                        if (excludedGameNames.Any(excl => gameName.Contains(excl, StringComparison.OrdinalIgnoreCase)))
                        {
                            excluded++;
                            Debug.WriteLine($"Excluding from display: {gameName} (Steam ID: {steamAppId})");
                            continue; // Skip adding to UI
                        }

                        if (!string.IsNullOrEmpty(executablePath))
                        {
                            gamesWithExecutable++;
                        }

                        Debug.WriteLine($"Adding: {gameName}");
                        Debug.WriteLine($"  Steam ID: {steamAppId}");
                        Debug.WriteLine($"  RAWG ID: {rawgId?.ToString() ?? "N/A"}");
                        Debug.WriteLine($"  Executable: {executablePath}");
                        Debug.WriteLine($"  Folder: {folderLocation}");

                        // Create Game object
                        var game = new Game
                        {
                            Name = gameName,
                            Executable = executablePath,
                            FolderLocation = folderLocation,
                            ImportedFrom = importSource,
                            SteamID = steamAppId,
                            RawgID = rawgId
                        };

                        // Add to collection
                        ViewModel.Games.Add(game);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error processing {gameName}: {ex.Message}");
                    }
                }

                // Show completion dialog
                var gamesWithRawg = ViewModel.Games.Count(g => g.RawgID.HasValue);
                var completionDialog = new ContentDialog
                {
                    Title = "Steam Library Scan Complete",
                    Content = $"Scanned: {totalScanned} items\n" +
                              $"Excluded: {excluded} (technical packages)\n" +
                              $"Displayed: {ViewModel.Games.Count} games\n\n" +
                              $"Games with Steam ID: {ViewModel.Games.Count}\n" +
                              $"Games with RAWG ID: {gamesWithRawg}\n" +
                              $"Games with Executable: {gamesWithExecutable}",
                    CloseButtonText = "Ok",
                    XamlRoot = this.Content.XamlRoot
                };

                await completionDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during scan: {ex.Message}");

                var errorDialog = new ContentDialog
                {
                    Title = "Scan Error",
                    Content = $"An error occurred during the scan:\n\n{ex.Message}",
                    CloseButtonText = "Ok",
                    XamlRoot = this.Content.XamlRoot
                };

                await errorDialog.ShowAsync();
            }
            finally
            {
                // Re-enable button
                if (button != null)
                {
                    button.IsEnabled = true;
                    button.Content = "Scan Games";
                }
            }
        }

        private async void ScanFolder_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Will be reimplemented later
            var dialog = new ContentDialog
            {
                Title = "Coming Soon",
                Content = "Folder scanning will be reimplemented soon!",
                CloseButtonText = "Ok",
                XamlRoot = this.Content.XamlRoot
            };

            await dialog.ShowAsync();
        }
    }
}

