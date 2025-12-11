using Codec.Models;
using Codec.Services;
using Codec.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        private double _mediaMaxHeight;

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

        private void DetailsLowerGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Track available height to avoid cutting media
            _mediaMaxHeight = Math.Max(0, e.NewSize.Height);
            UpdateMediaHeight();
        }

        private void HeroOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Scale logo padding with window size (clamped for usability)
            double padding = Math.Clamp(e.NewSize.Width * 0.03, 20, 64);
            if (HeroLogo != null)
            {
                HeroLogo.Margin = new Thickness(padding, padding, 0, 0);
                // Scale logo size with window width, within reasonable bounds
                double targetHeight = Math.Clamp(e.NewSize.Width * 0.18, 170, 320);
                HeroLogo.Height = targetHeight;
                HeroLogo.MaxWidth = Math.Clamp(targetHeight * 2.2, 360, 640);
            }
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

        private void MediaContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateMediaHeight(e.NewSize.Width, null);
        }

        private void UpdateMediaHeight(double? widthOverride = null, double? heightOverride = null)
        {
            if (MediaContainer == null || MediaFlipView == null)
            {
                return;
            }

            var width = widthOverride ?? MediaContainer.ActualWidth;
            var availableHeight = heightOverride ?? (_mediaMaxHeight > 0 ? _mediaMaxHeight : MediaContainer.ActualHeight);

            if (width <= 0 || availableHeight <= 0)
            {
                return;
            }

            // Ideal 16:9 based on width
            double targetWidth = width;
            double targetHeight = targetWidth * 0.5625;

            // If height is insufficient, reduce width to fit height while keeping 16:9
            if (targetHeight > availableHeight)
            {
                targetHeight = availableHeight;
                targetWidth = Math.Min(width, availableHeight / 0.5625);
            }

            MediaFlipView.Width = targetWidth;
            MediaFlipView.Height = targetHeight;
        }

        private async void AddGame_Click(object sender, RoutedEventArgs e)
        {
            await RunWithFooterLockAsync(AddGameAsync);
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
            string bestName = GameNameService.GetBestName(exeFile.Path) ?? "Unknown";

            var testDialog = new ContentDialog
            {
                Title = "GameName and ID Service",
                Content = $"Name: {bestName}\n" +
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
            }

            SetFooterButtonsEnabled(false);
            ShowScanProgress("Looking for Games (this might take a few minutes)...", isIndeterminate: true);

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
                SetFooterButtonsEnabled(true);

                if (button != null)
                {
                    button.IsEnabled = true;
                    button.Content = "Scan for Games";
                }
            }
        }

        private async void Debug_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetFooterButtonsEnabled(false);
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
                        var displayValue = string.IsNullOrWhiteSpace(value) ? "N/A" : value;
                        var v = new TextBlock { Text = displayValue, TextWrapping = TextWrapping.Wrap };
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
                    AddRow("Genres", g.Genres != null && g.Genres.Count > 0 ? string.Join(", ", g.Genres) : null);
                    AddRow("Price", g.Price);
                    AddRow("Age Rating", g.AgeRating);
                    AddRow("Description", g.Description);
                    AddRow("Release Date", g.ReleaseDate?.ToString());
                    AddRow("Steam Rating", g.SteamRating?.ToString());
                    AddRow("Main Story (sec)", g.TimeToCompleteMainStory?.ToString());
                    AddRow("Completionist (sec)", g.TimeToCompleteCompletionist?.ToString());
                    AddRow("Capsule", g.LibCapsule);
                    AddRow("Hero", g.LibHero);
                    AddRow("Logo", g.LibLogo);
                    AddRow("Icon", g.LibIcon);
                    AddRow("Client Icon", g.LibClientIcon);
                    AddRow("Media", g.Media != null && g.Media.Count > 0 ? string.Join(", ", g.Media) : "");
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
                    Title = "Scanned Games Data",
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
            finally
            {
                SetFooterButtonsEnabled(true);
            }
        }

        private async void RefreshCovers_Click(object sender, RoutedEventArgs e)
        {
            var refreshBtn = sender as Button;
            try
            {
                SetFooterButtonsEnabled(false);
                if (refreshBtn != null) refreshBtn.IsEnabled = false;

                ShowScanProgress("Fetching Covers...", ViewModel.Games.Count == 0);
                PrepareCoverProgress(ViewModel.Games.Count, "Update Cover", "No Games to update.");

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
                SetFooterButtonsEnabled(true);
                if (refreshBtn != null) refreshBtn.IsEnabled = true;
            }
        }

        private async void ResetApp_Click(object sender, RoutedEventArgs e)
        {
            var confirmationDialog = new ContentDialog
            {
                Title = "Reset Codec",
                Content = "This will delete all saved Data. Continue?",
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

                SetFooterButtonsEnabled(false);

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
                SetFooterButtonsEnabled(true);
            }

            if (resetSuccessful)
            {
                var successDialog = new ContentDialog
                {
                    Title = "Codec Reset",
                    Content = "All Data has been successfully deleted.",
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

        private void GameItem_Click(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Game game)
            {
                _ = FetchAndShowDetailsAsync(game);
            }
        }

        private async Task FetchAndShowDetailsAsync(Game game)
        {
            ShowDetailLoading(true);

            try
            {
                var rawgTask = game.RawgID.HasValue ? RawgDetailsService.PopulateAsync(game) : Task.CompletedTask;
                var steamTask = game.SteamID.HasValue ? SteamDetailsService.PopulateFromSteamAsync(game) : Task.CompletedTask;
                var hltbTask = HltbService.PopulateAsync(game, DispatcherQueue); // do not block spinner on HLTB

                // Ensure placeholder links are present for visibility
                if (string.IsNullOrWhiteSpace(game.RawgUrl))
                {
                    game.RawgUrl = "https://rawg.io";
                }
                if (string.IsNullOrWhiteSpace(game.HltbUrl))
                {
                    game.HltbUrl = "https://howlongtobeat.com";
                }

                await Task.WhenAll(rawgTask, steamTask);

                ViewModel.SelectedGame = game;
                DetailsView.DataContext = game;

                await AwaitHeroAndLogoAsync(game);

                DetailsView.Visibility = Visibility.Visible;
                LibraryGrid.Visibility = Visibility.Collapsed;
                BottomBar.Visibility = Visibility.Collapsed;
                AppTitleBar.Visibility = Visibility.Collapsed;

                    _ = hltbTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            Debug.WriteLine($"HLTB fetch failed: {t.Exception?.GetBaseException().Message}");
                        }
                    }, TaskContinuationOptions.ExecuteSynchronously);
            }
            finally
            {
                ShowDetailLoading(false);
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            DetailsView.Visibility = Visibility.Collapsed;
            DetailsView.DataContext = null;
            LibraryGrid.Visibility = Visibility.Visible;
            BottomBar.Visibility = Visibility.Visible;
            AppTitleBar.Visibility = Visibility.Visible;
            ViewModel.SelectedGame = null;
        }

        private void SetFooterButtonsEnabled(bool isEnabled)
        {
            ScanGamesButton.IsEnabled = isEnabled;
            TestExecutableButton.IsEnabled = isEnabled;
            DebugButton.IsEnabled = isEnabled;
            RefreshCoversButton.IsEnabled = isEnabled;
            ResetAppButton.IsEnabled = isEnabled;
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
                {
                    return;
                }

                await Task.Delay(stepMs);
                waited += stepMs;
            }
        }

        private void ShowDetailLoading(bool show)
        {
            DetailLoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task RunWithFooterLockAsync(Func<Task> action)
        {
            SetFooterButtonsEnabled(false);
            try
            {
                await action();
            }
            finally
            {
                SetFooterButtonsEnabled(true);
            }
        }
    }
}

