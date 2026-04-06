using Codec.Models;
using Codec.Services;
using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Codec.Views
{
    public sealed partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            InitializeComponent();
            Title = "Codec";
            ViewModel = new MainViewModel();
            RootGrid.DataContext = ViewModel;
            ExtendsContentIntoTitleBar = true;
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico"));
            _ = ViewModel.LoadLibraryAsync();
        }

        private void SidebarGameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListView list && list.SelectedItem is Game game)
                ViewModel.SelectGameCommand.Execute(game);
        }

        private async void SidebarAddGames_Click(object sender, RoutedEventArgs e)
            => await ViewModel.ScanGamesCommand.ExecuteAsync(null);

        private async void OnboardingStart_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsOnboardingVisible = false;
            await ViewModel.ScanGamesCommand.ExecuteAsync(null);
        }

        private void OnboardingSkip_Click(object sender, RoutedEventArgs e)
            => ViewModel.IsOnboardingVisible = false;

        private async void AddGame_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsUiEnabled = false;
            try
            {
                var exePicker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                exePicker.FileTypeFilter.Add(".exe");
                InitializeWithWindow.Initialize(exePicker, WindowNative.GetWindowHandle(this));

                var exeFile = await exePicker.PickSingleFileAsync();
                if (exeFile == null)
                    return;

                Debug.WriteLine($"Starting ID lookup for: {exeFile.Path}");
                var (steamId, rawgId) = await GameNameService.FindGameIdsAsync(exeFile.Path);

                string steamIdText = steamId.HasValue ? steamId.Value.ToString() : "Not found";
                string rawgIdText = rawgId.HasValue ? rawgId.Value.ToString() : "Not found";
                string bestName = GameNameService.GetBestName(exeFile.Path) ?? "Unknown";

                var testDialog = new ContentDialog
                {
                    Title = "GameName and ID Service",
                    Content = $"Name: {bestName}\nSteam ID: {steamIdText}\nRAWG ID: {rawgIdText}",
                    CloseButtonText = "Ok",
                    XamlRoot = Content.XamlRoot
                };
                await testDialog.ShowAsync();
            }
            finally
            {
                ViewModel.IsUiEnabled = true;
            }
        }

        private async void Debug_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsUiEnabled = false;
            try
            {
                var panel = new StackPanel { Spacing = 8, MinWidth = 360 };

                static string? FormatList(System.Collections.Generic.IEnumerable<string>? values) =>
                    values != null && values.Any() ? string.Join(", ", values) : null;

                foreach (var g in ViewModel.Games)
                {
                    var expander = new Expander { Header = g.Name, IsExpanded = false };
                    var grid = new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(180) },
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                        }
                    };

                    int row = 0;
                    void AddRow(string key, string? value)
                    {
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        var k = new TextBlock { Text = key, Opacity = 0.6 };
                        var v = new TextBlock { Text = string.IsNullOrWhiteSpace(value) ? "N/A" : value, TextWrapping = TextWrapping.Wrap };
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
                    AddRow("Capsule", g.LibCapsule);
                    AddRow("Hero", g.LibHero);
                    AddRow("Logo", g.LibLogo);
                    AddRow("Media", FormatList(g.Media));
                    AddRow("Official Website", g.OfficialWebsiteUrl);
                    AddRow("Steam Page", g.SteamPageUrl);
                    AddRow("RAWG Page", g.RawgUrl);
                    AddRow("HowLongToBeat", g.HltbUrl);

                    expander.Content = grid;
                    panel.Children.Add(expander);
                }

                var container = new Grid { MaxWidth = 800, MaxHeight = 540 };
                container.Children.Add(new ScrollViewer
                {
                    Content = panel,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                });

                var dialog = new ContentDialog
                {
                    Title = "Scanned Games Data",
                    Content = container,
                    CloseButtonText = "Close",
                    XamlRoot = Content.XamlRoot
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
                    XamlRoot = Content.XamlRoot
                };
                await error.ShowAsync();
            }
            finally
            {
                ViewModel.IsUiEnabled = true;
            }
        }

        private async void RefreshCovers_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsUiEnabled = false;
            try
            {
                await ViewModel.RefreshCoversAsync();
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "Refresh Covers Error",
                    Content = ex.Message,
                    CloseButtonText = "Close",
                    XamlRoot = Content.XamlRoot
                };
                await errorDialog.ShowAsync();
            }
            finally
            {
                ViewModel.IsUiEnabled = true;
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
                XamlRoot = Content.XamlRoot
            };

            var result = await confirmationDialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            bool resetSuccessful = false;
            try
            {
                ViewModel.IsAppSpinnerActive = true;
                ViewModel.IsUiEnabled = false;

                ViewModel.IsDetailsVisible = false;
                ViewModel.SelectedGame = null;
                ViewModel.SidebarSelectedItem = null;
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
                    XamlRoot = Content.XamlRoot
                };
                await errorDialog.ShowAsync();
            }
            finally
            {
                ViewModel.IsAppSpinnerActive = false;
                ViewModel.IsUiEnabled = true;
            }

            if (resetSuccessful)
            {
                var successDialog = new ContentDialog
                {
                    Title = "Codec Reset",
                    Content = "All saved data and cached content have been deleted.",
                    CloseButtonText = "Close",
                    XamlRoot = Content.XamlRoot
                };
                await successDialog.ShowAsync();
                ViewModel.IsOnboardingVisible = true;
                ViewModel.SetLoadingState(false);
            }
        }
    }
}
