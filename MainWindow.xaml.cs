using Codec.Models;
using Codec.Services;
using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using System;
using System.Collections.Generic;
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

            LibraryStorageService.EnsureStorageInitialized();

            // Load persisted library
            _ = LoadLibraryAsync();
        }

        private async Task LoadLibraryAsync()
        {
            var saved = await LibraryStorageService.LoadAsync();
            await EnsureCoversAsync(saved);

            ViewModel.Games.Clear();
            foreach (var g in saved)
            {
                ViewModel.Games.Add(g);
            }
            await LibraryStorageService.SaveAsync(ViewModel.Games);
        }

        private static bool IsPlaceholder(string? uri) => string.IsNullOrWhiteSpace(uri) || uri.StartsWith("https://placehold.co/", StringComparison.OrdinalIgnoreCase);
        private static bool LocalFileMissing(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return true;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return true;
            if (parsed.IsFile)
            {
                try { return !System.IO.File.Exists(parsed.LocalPath); } catch { return true; }
            }
            return false; // non-file URIs are considered present
        }

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
                        {
                            g.LibCapsule = cover;
                        }
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
            if (button != null)
            {
                button.IsEnabled = false;
                button.Content = "Scanning...";
            }

            AddGamesButton.IsEnabled = false;
            ShowScanProgress("Durchsuche Bibliotheken...", isIndeterminate: true);

            try
            {
                var progress = new Progress<string>(_ => { /* suppress per-item status on button */ });

                var scanner = new GameScanner();
                var scanResults = await scanner.ScanAllGamesAsync(progress);

                // Prepare new list
                var newGames = new List<Game>();

                // Games to exclude from display (but still scan)
                var excludedGameNames = new[] { "Steamworks Common Redistributables", "Steam Linux Runtime", "Proton", "Steam Audio", "Steam VR" };

                foreach (var (steamAppId, gameName, rawgId, importSource, executablePath, folderLocation) in scanResults)
                {
                    try
                    {
                        if (excludedGameNames.Any(excl => gameName.Contains(excl, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        var game = new Game
                        {
                            Name = gameName,
                            Executable = executablePath,
                            FolderLocation = folderLocation,
                            ImportedFrom = importSource,
                            SteamID = steamAppId,
                            RawgID = rawgId
                        };

                        newGames.Add(game);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error processing {gameName}: {ex.Message}");
                    }
                }

                await PopulateGridDbDataAsync(newGames);

                int totalGames = newGames.Count;
                PrepareCoverProgress(totalGames);

                ViewModel.Games.Clear();
                int processed = 0;
                foreach (var g in newGames)
                {
                    await EnsureCoverForGameAsync(g);
                    ViewModel.Games.Add(g);
                    processed++;
                    UpdateCoverProgress(processed, totalGames);
                }

                // Persist to disk after covers are set
                await LibraryStorageService.SaveAsync(ViewModel.Games);
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
                HideScanProgress();
                AddGamesButton.IsEnabled = true;

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
                    menuDialog.Hide();
                    await ScanGamesAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in scan button click: {ex.Message}");
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

        private async void Debug_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Build a compact, readable view of current games
                var panel = new StackPanel { Spacing = 8, MinWidth = 360 };

                foreach (var g in ViewModel.Games)
                {
                    var expander = new Expander
                    {
                        Header = $"{g.Name} (Steam: {g.SteamID?.ToString() ?? "N/A"}, RAWG: {g.RawgID?.ToString() ?? "N/A"})",
                        IsExpanded = false
                    };

                    var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(180) }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } } };

                    int row = 0;
                    void AddRow(string key, string? value)
                    {
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        var k = new TextBlock { Text = key, Opacity = 0.6 };
                        var v = new TextBlock { Text = value ?? "", TextWrapping = TextWrapping.Wrap };
                        Grid.SetRow(k, row); Grid.SetColumn(k, 0);
                        Grid.SetRow(v, row); Grid.SetColumn(v, 1);
                        grid.Children.Add(k); grid.Children.Add(v);
                        row++;
                    }

                    AddRow("ID", g.Id.ToString());
                    AddRow("Date Added", g.DateAdded.ToString());
                    AddRow("Import Source", g.ImportedFrom);
                    AddRow("Steam ID", g.SteamID?.ToString());
                    AddRow("RAWG ID", g.RawgID?.ToString());
                    AddRow("Executable", g.Executable);
                    AddRow("Folder Location", g.FolderLocation);
                    AddRow("Folder Size", g.FolderSize.ToString());
                    AddRow("Publisher", g.Publisher);
                    AddRow("Developer", g.Developer);
                    AddRow("Release Date", g.ReleaseDate?.ToString());
                    AddRow("Steam Rating", g.SteamRating?.ToString());
                    AddRow("Age Rating", g.AgeRating);
                    AddRow("Main Story (sec)", g.TimeToCompleteMainStory?.ToString());
                    AddRow("Completionist (sec)", g.TimeToCompleteCompletionist?.ToString());
                    AddRow("Capsule", g.LibCapsule);
                    AddRow("Hero", g.LibHero);
                    AddRow("Logo", g.LibLogo);
                    AddRow("Icon", g.LibIcon);
                    AddRow("Client Icon", g.LibClientIcon);
                    AddRow("Screenshots", g.Screenshots != null && g.Screenshots.Count > 0 ? string.Join(", ", g.Screenshots) : "");
                    AddRow("Last Updated", g.LastUpdated?.ToString());
                    AddRow("Last Launched", g.LastLaunched?.ToString());

                    expander.Content = grid;
                    panel.Children.Add(expander);
                }

                var scroller = new ScrollViewer
                {
                    Content = panel,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };

                var container = new Grid
                {
                    MaxWidth = 800,
                    MaxHeight = 540
                };
                container.Children.Add(scroller);

                var dialog = new ContentDialog
                {
                    Title = "Debug: Scanned Games Data",
                    Content = container,
                    CloseButtonText = "Close",
                    XamlRoot = this.Content.XamlRoot
                };

                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var error = new ContentDialog
                {
                    Title = "Debug Error",
                    Content = ex.Message,
                    CloseButtonText = "Close",
                    XamlRoot = this.Content.XamlRoot
                };
                await error.ShowAsync();
            }
        }

        private async void RefreshCovers_Click(object sender, RoutedEventArgs e)
        {
            var refreshBtn = sender as Button;
            try
            {
                AddGamesButton.IsEnabled = false;
                DebugButton.IsEnabled = false;
                if (refreshBtn != null) refreshBtn.IsEnabled = false;

                ShowScanProgress("Aktualisiere Cover...", ViewModel.Games.Count == 0);
                PrepareCoverProgress(ViewModel.Games.Count, "Aktualisiere Cover", "Keine Spiele zum Aktualisieren.");

                int processed = 0;
                foreach (var g in ViewModel.Games)
                {
                    if (g.SteamID.HasValue)
                    {
                        var cover = await GameAssetService.DownloadSteamLibraryCoverAsync(g.SteamID.Value, force: true);
                        if (!string.IsNullOrEmpty(cover))
                        {
                            g.LibCapsule = cover;
                        }
                        await Task.Delay(75);
                    }
                    else
                    {
                        await GridDbService.TryPopulateGridAssetsAsync(g, forceCoverDownload: true);
                        await Task.Delay(75);
                    }

                    processed++;
                    UpdateCoverProgress(processed, ViewModel.Games.Count, "Aktualisiere Cover");
                }

                await LibraryStorageService.SaveAsync(ViewModel.Games);
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "Refresh Covers Error",
                    Content = ex.Message,
                    CloseButtonText = "Close",
                    XamlRoot = this.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
            }
            finally
            {
                HideScanProgress();
                AddGamesButton.IsEnabled = true;
                DebugButton.IsEnabled = true;
                if (refreshBtn != null) refreshBtn.IsEnabled = true;
            }
        }
        
        private async void ResetApp_Click(object sender, RoutedEventArgs e)
        {
            var confirmationDialog = new ContentDialog
            {
                Title = "Reset Codec",
                Content = "This will delete the entire saved library and all downloaded assets from this device. Continue?",
                PrimaryButtonText = "Delete Data",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await confirmationDialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            bool resetSuccessful = false;
            try
            {
                AppSpinner.Visibility = Visibility.Visible;
                AppSpinner.IsActive = true;

                AddGamesButton.IsEnabled = false;
                DebugButton.IsEnabled = false;
                RefreshCoversButton.IsEnabled = false;
                ResetAppButton.IsEnabled = false;

                await LibraryStorageService.ResetAsync();
                ViewModel.Games.Clear();
                resetSuccessful = true;
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "Reset Error",
                    Content = ex.Message,
                    CloseButtonText = "Close",
                    XamlRoot = this.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
            }
            finally
            {
                AppSpinner.IsActive = false;
                AppSpinner.Visibility = Visibility.Collapsed;
                AddGamesButton.IsEnabled = true;
                DebugButton.IsEnabled = true;
                RefreshCoversButton.IsEnabled = true;
                ResetAppButton.IsEnabled = true;
            }

            if (resetSuccessful)
            {
                var successDialog = new ContentDialog
                {
                    Title = "Codec Reset",
                    Content = "All stored data has been deleted. You can now start fresh.",
                    CloseButtonText = "Close",
                    XamlRoot = this.Content.XamlRoot
                };
                await successDialog.ShowAsync();
            }
        }

        private void ShowScanProgress(string message, bool isIndeterminate)
        {
            ScanProgressPanel.Visibility = Visibility.Visible;
            ScanProgressText.Text = message;
            ScanProgressBar.IsIndeterminate = isIndeterminate;
            ScanProgressBar.Value = 0;
            ScanProgressBar.Maximum = 1;
        }

        private void PrepareCoverProgress(int totalGames, string? labelPrefix = null, string? emptyMessage = null)
        {
            if (totalGames <= 0)
            {
                ScanProgressBar.IsIndeterminate = true;
                ScanProgressText.Text = emptyMessage ?? "Keine neuen Spiele gefunden.";
                return;
            }

            ScanProgressBar.IsIndeterminate = false;
            ScanProgressBar.Minimum = 0;
            ScanProgressBar.Maximum = totalGames;
            ScanProgressBar.Value = 0;
            var prefix = labelPrefix ?? "Lade Cover";
            ScanProgressText.Text = $"{prefix} (0/{totalGames})";
        }

        private void UpdateCoverProgress(int processed, int total, string? labelPrefix = null)
        {
            if (total <= 0)
            {
                ScanProgressText.Text = labelPrefix ?? "Lade Cover";
                return;
            }

            ScanProgressBar.IsIndeterminate = false;
            ScanProgressBar.Value = Math.Min(processed, total);
            var prefix = labelPrefix ?? "Lade Cover";
            ScanProgressText.Text = $"{prefix} ({Math.Min(processed, total)}/{total})";
        }

        private void HideScanProgress()
        {
            ScanProgressPanel.Visibility = Visibility.Collapsed;
        }

        private Task EnsureCoverForGameAsync(Game game)
        {
            return EnsureCoversAsync(new[] { game });
        }

        private static async Task PopulateGridDbDataAsync(IEnumerable<Game> games)
        {
            foreach (var game in games)
            {
                if (game.SteamID.HasValue)
                {
                    continue;
                }

                await GridDbService.TryPopulateGridAssetsAsync(game);
                await Task.Delay(75);
            }
        }
    }
}

