using Codec.Models;
using Codec.Services;
using Codec.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
            this.Title = "Codec";
            ViewModel = new MainViewModel();
            RootGrid.DataContext = ViewModel;
            ExtendsContentIntoTitleBar = true;

            string iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico");
            this.AppWindow.SetIcon(iconPath);

            LibraryStorageService.EnsureStorageInitialized();
            ViewModel.SetLoadingState(true, "Loading your library...", "Preparing Codec");

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
            QueueBackgroundPrefetch(ViewModel.Games);

            ViewModel.SetLoadingState(false);
            ViewModel.IsInitialLoading = false;
            ViewModel.IsOnboardingVisible = ViewModel.Games.Count == 0;
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

        private async void SidebarScan_Click(object sender, RoutedEventArgs e)
        {
            await ScanGamesAsync(showFullScreenLoading: true);
        }

        private async Task ScanGamesAsync(Button? button = null, bool showFullScreenLoading = false)
        {
            if (button != null)
            {
                button.IsEnabled = false;
            }

            SetFooterButtonsEnabled(false);

            if (showFullScreenLoading)
            {
                ViewModel.SetLoadingState(true, "Finding your games...", "This will take a few minutes, depending on system size.");
            }
            else
            {
                ShowScanProgress("Looking for Games (this might take a few minutes)...", isIndeterminate: true);
            }

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

                // Fallback RAWG ID population for games still missing one
                var fallbackTasks = newGames
                    .Where(g => !g.RawgID.HasValue)
                    .Select(g => RawgDetailsService.TryPopulateRawgFromSearchAsync(g));
                await Task.WhenAll(fallbackTasks);

                await PopulateGridDbDataAsync(newGames);

                int totalGames = newGames.Count;
                if (!showFullScreenLoading)
                {
                    PrepareCoverProgress(totalGames);
                }

                ViewModel.Games.Clear();
                int processed = 0;
                foreach (var g in newGames)
                {
                    await EnsureCoverForGameAsync(g);
                    ViewModel.Games.Add(g);
                    processed++;
                    if (!showFullScreenLoading)
                    {
                        UpdateCoverProgress(processed, totalGames);
                    }
                }

                // Persist to disk after covers are set
                await LibraryStorageService.SaveAsync(ViewModel.Games);
                QueueBackgroundPrefetch(ViewModel.Games);
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
                if (showFullScreenLoading)
                {
                    ViewModel.SetLoadingState(false);
                    ViewModel.IsOnboardingVisible = ViewModel.Games.Count == 0;
                }
                else
                {
                    HideScanProgress();
                }

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

                static string? FormatList(IEnumerable<string>? values) => values != null && values.Any() ? string.Join(", ", values) : null;

                foreach (var g in ViewModel.Games)
                {
                    var expander = new Expander
                    {
                        Header = g.Name,
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
                    AddRow("Name", g.Name);
                    AddRow("Date Added", g.DateAdded.ToString());
                    AddRow("Import Source", g.ImportedFrom);
                    AddRow("Import Source Display", g.ImportedFromDisplay);
                    AddRow("Executable", g.Executable);
                    AddRow("Folder Location", g.FolderLocation);
                    AddRow("Folder Size (bytes)", g.FolderSize.ToString());
                    AddRow("Steam ID", g.SteamID?.ToString());
                    AddRow("RAWG ID", g.RawgID?.ToString());
                    AddRow("RAWG Slug", g.RawgSlug);
                    AddRow("GridDB ID", g.GridDbId?.ToString());
                    AddRow("Publisher", g.Publisher);
                    AddRow("Developer", g.Developer);
                    AddRow("Genres", FormatList(g.Genres));
                    AddRow("Categories", FormatList(g.Categories));
                    AddRow("Platforms", FormatList(g.Platforms));
                    AddRow("Price", g.Price);
                    AddRow("Price Discount", g.PriceDiscount);
                    AddRow("Description", g.Description);
                    AddRow("Release Date", g.ReleaseDate?.ToString());
                    AddRow("Steam Rating", g.SteamRating?.ToString());
                    AddRow("Steam Review Summary", g.SteamReviewSummary);
                    AddRow("Steam Review Total", g.SteamReviewTotal?.ToString());
                    AddRow("Age Rating", g.AgeRating);
                    AddRow("Main Story (sec)", g.TimeToCompleteMainStory?.ToString());
                    AddRow("Completionist (sec)", g.TimeToCompleteCompletionist?.ToString());
                    AddRow("Play Time (sec)", g.PlayTime?.ToString());
                    AddRow("Launch Script", g.LaunchScript);
                    AddRow("Capsule", g.LibCapsule);
                    AddRow("Hero", g.LibHero);
                    AddRow("Logo", g.LibLogo);
                    AddRow("Icon", g.LibIcon);
                    AddRow("Client Icon", g.LibClientIcon);
                    AddRow("Media", FormatList(g.Media));
                    AddRow("Official Website", g.OfficialWebsiteUrl);
                    AddRow("Steam Page", g.SteamPageUrl);
                    AddRow("RAWG Page", g.RawgUrl);
                    AddRow("HowLongToBeat", g.HltbUrl);
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
                Content = "This will delete all saved data and cached files. Continue?",
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

                // Release bindings and clear UI to drop any file handles before deletion
                DetailsView.Visibility = Visibility.Collapsed;
                DetailsView.DataContext = null;
                LibraryGrid.Visibility = Visibility.Visible;
                ViewModel.SelectedGame = null;

                ViewModel.Games.Clear();

                DataCacheService.Clear();
                await LibraryStorageService.ResetAsync();
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
                    Content = "All saved data and cached content have been deleted.",
                    CloseButtonText = "Close",
                    XamlRoot = this.Content.XamlRoot
                };
                await successDialog.ShowAsync();
                ViewModel.IsOnboardingVisible = true;
                ViewModel.SetLoadingState(false);
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

        private void LibraryItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var game = fe.Tag as Game ?? fe.DataContext as Game;
                if (game != null)
                {
                    _ = FetchAndShowDetailsAsync(game);
                }
            }
        }

        private async Task FetchAndShowDetailsAsync(Game game)
        {
            ShowDetailLoading(true);

            var folderSizeTask = FolderSizeService.CalculateAsync(game.FolderLocation);

            try
            {
                var rawgTask = game.RawgID.HasValue ? RawgDetailsService.PopulateAsync(game) : Task.CompletedTask;
                var steamTask = game.SteamID.HasValue ? SteamDetailsService.PopulateFromSteamAsync(game) : Task.CompletedTask;
                var hltbTask = HltbService.PopulateAsync(game, DispatcherQueue); // do not block spinner on HLTB

                await Task.WhenAll(rawgTask, steamTask);

                // Link hydration: only surface links once data fetch completed
                if (string.IsNullOrWhiteSpace(game.RawgUrl))
                {
                    string? rawgUrl = null;

                    if (!string.IsNullOrWhiteSpace(game.RawgSlug))
                    {
                        rawgUrl = $"https://rawg.io/games/{game.RawgSlug}";
                    }
                    else if (game.RawgID.HasValue)
                    {
                        rawgUrl = $"https://rawg.io/games/{game.RawgID.Value}";
                    }

                    game.RawgUrl = rawgUrl ?? "https://rawg.io";
                    game.NotifyPropertyChanged(nameof(Game.RawgUrl));
                }
                if (string.IsNullOrWhiteSpace(game.HltbUrl))
                {
                    game.HltbUrl = "https://howlongtobeat.com";
                    game.NotifyPropertyChanged(nameof(Game.HltbUrl));
                }

                ViewModel.SelectedGame = game;
                DetailsView.DataContext = game;

                _ = folderSizeTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Debug.WriteLine($"Folder size fetch failed for {game.Name}: {t.Exception?.GetBaseException().Message}");
                        return;
                    }

                    var size = t.Result;
                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        if (game.FolderSize != size)
                        {
                            game.FolderSize = size;
                            game.NotifyPropertyChanged(nameof(Game.FolderSize));
                            _ = LibraryStorageService.SaveAsync(ViewModel.Games);
                        }
                    });
                }, TaskScheduler.Default);

                await AwaitHeroAndLogoAsync(game);

                DetailsView.Visibility = Visibility.Visible;
                LibraryGrid.Visibility = Visibility.Collapsed;

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
            var list = FindElement<ListView>("SidebarGameList");
            if (list != null)
            {
                list.SelectedItem = null;
            }
        }

        private void SetFooterButtonsEnabled(bool isEnabled)
        {
            var debugButton = FindElement<Button>("SidebarDebugButton");
            var addButton = FindElement<Button>("SidebarAddButton");
            var list = FindElement<ListView>("SidebarGameList");

            if (debugButton != null) debugButton.IsEnabled = isEnabled;
            if (addButton != null) addButton.IsEnabled = isEnabled;
            if (list != null) list.IsEnabled = isEnabled;
        }

        private void SidebarGameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListView list && list.SelectedItem is Game game)
            {
                _ = FetchAndShowDetailsAsync(game);
            }
        }

        private async void SidebarAddGames_Click(object sender, RoutedEventArgs e)
        {
            await ScanGamesAsync(showFullScreenLoading: true);
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

        private async void OnboardingStart_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsOnboardingVisible = false;
            await ScanGamesAsync(showFullScreenLoading: true);
        }

        private void OnboardingSkip_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsOnboardingVisible = false;
        }

        private T? FindElement<T>(string name) where T : class
        {
            return (Content as FrameworkElement)?.FindName(name) as T;
        }

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
            if (!game.SteamID.HasValue)
            {
                return;
            }

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
            if (string.IsNullOrWhiteSpace(game.Name))
            {
                return;
            }

            string normalized = NormalizeForHltb(game.Name);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                DataCacheService.QueueWarmup($"https://codec-api-proxy.vercel.app/api/hltb/search?term={Uri.EscapeDataString(normalized)}");
            }
        }

        private static string NormalizeForHltb(string value)
        {
            string cleaned = Regex.Replace(value, "[^a-zA-Z0-9 ]", " ");
            cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
            return cleaned;
        }
    }
}

