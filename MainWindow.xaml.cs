using Codec.Models;
using Codec.Services;
using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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
            await AddGameAsync();
        }

        private async Task AddGameAsync()
        {
            var exePicker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            exePicker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(exePicker, WindowNative.GetWindowHandle(this));

            var exeFile = await exePicker.PickSingleFileAsync();
            if (exeFile == null)
                return; // User cancelled the picker

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
            await ScanGamesAsync(sender as Button);
        }

        private async Task ScanGamesAsync(Button? button = null)
        {
            // Clear previous results
            ViewModel.Games.Clear();

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

                // Start comprehensive game scan using new 3-phase architecture
                var scanner = new GameScanner();
                var scanResults = await scanner.ScanAllGamesAsync(progress);

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
                        Debug.WriteLine($"  Steam ID: {steamAppId?.ToString() ?? "N/A"}");
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
                var gamesWithSteam = ViewModel.Games.Count(g => g.SteamID.HasValue);
                var completionDialog = new ContentDialog
                {
                    Title = "Game Library Scan Complete",
                    Content = $"Scanned: {totalScanned} items\n" +
             $"Excluded: {excluded} (technical packages)\n" +
                  $"Displayed: {ViewModel.Games.Count} games\n\n" +
                  $"Games with Steam ID: {gamesWithSteam}\n" +
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

        /// <summary>
        /// Shows popup menu with game adding options (custom minimal UI)
        /// </summary>
        private async void AddGames_Click(object sender, RoutedEventArgs e)
        {
            // Build a minimal popup with only 3 buttons and a loading spinner
            var spinner = new ProgressRing
            {
                IsActive = false,
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = Visibility.Collapsed
            };

            var scanButton = new Button
            {
                Content = "Scan PC for Games",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var addExeButton = new Button
            {
                Content = "Add Executable",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 12)
            };
            header.Children.Add(spinner);
            header.Children.Add(new TextBlock { Text = string.Empty, VerticalAlignment = VerticalAlignment.Center });

            var content = new StackPanel
            {
                Spacing = 8,
                MinWidth = 320
            };
            content.Children.Add(header);
            content.Children.Add(scanButton);
            content.Children.Add(addExeButton);
            content.Children.Add(cancelButton);

            var menuDialog = new ContentDialog
            {
                Title = null,
                Content = content,
                // Hide default dialog buttons; we use our own
                PrimaryButtonText = string.Empty,
                SecondaryButtonText = string.Empty,
                CloseButtonText = string.Empty,
                XamlRoot = this.Content.XamlRoot
            };

            scanButton.Click += async (_, __) =>
            {
                try
                {
                    // Close popup and show app-level spinner
                    menuDialog.Hide();
                    AppSpinner.Visibility = Visibility.Visible;
                    AppSpinner.IsActive = true;

                    // Disable global Add Games button while scanning
                    AddGamesButton.IsEnabled = false;

                    await ScanGamesAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in scan button click: {ex.Message}");
                }
                finally
                {
                    AppSpinner.IsActive = false;
                    AppSpinner.Visibility = Visibility.Collapsed;
                    AddGamesButton.IsEnabled = true;
                }
            };

            addExeButton.Click += async (_, __) =>
            {
                try
                {
                    // Close popup and show app-level spinner
                    menuDialog.Hide();
                    AppSpinner.Visibility = Visibility.Visible;
                    AppSpinner.IsActive = true;

                    // Disable global Add Games button during operation
                    AddGamesButton.IsEnabled = false;

                    await AddGameAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in add executable button click: {ex.Message}");
                }
                finally
                {
                    AppSpinner.IsActive = false;
                    AppSpinner.Visibility = Visibility.Collapsed;
                    AddGamesButton.IsEnabled = true;
                }
            };

            cancelButton.Click += (_, __) =>
            {
                menuDialog.Hide();
            };

            await menuDialog.ShowAsync();
        }

    }
}

