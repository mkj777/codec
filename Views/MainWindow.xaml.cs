using Codec.Models;
using Codec.Services;
using Codec.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Codec.Views
{
    public sealed partial class MainWindow : Window
    {
        private const int MinWindowWidth = 900;
        private const int MinWindowHeight = 560;
        private static readonly SolidColorBrush SidebarSelectedForegroundBrush = new(ColorHelper.FromArgb(0xFF, 0xF3, 0xED, 0xC9));
        private static readonly SolidColorBrush SidebarUnselectedForegroundBrush = new(ColorHelper.FromArgb(0xFF, 0x9E, 0x8E, 0x78));

        private Storyboard? _sidebarStateStoryboard;
        private readonly TaskCompletionSource<bool> _libraryReadyForStartup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _startupSequenceStarted;
        private bool _isResetting;

        public MainViewModel ViewModel { get; }

        public string AppVersion { get; } =
            $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}";


        public MainWindow()
        {
            InitializeComponent();
            Title = "Codec";
            ViewModel = new MainViewModel(App.Services);
            RootGrid.DataContext = ViewModel;
            ExtendsContentIntoTitleBar = true;
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico"));
            ConfigureWindowConstraints();
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RootGrid_KeyDown), true);
            RootGrid.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(RootGrid_RightTapped), true);
            RootGrid.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RootGrid.Loaded -= MainWindow_Loaded;
            _ = ViewModel.LoadLibraryAsync();
            _ = PlayStartupOpenAnimationAsync();

            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                AdminWarningToast.Visibility = Visibility.Visible;
        }

        private void AdminWarningClose_Click(object sender, RoutedEventArgs e)
            => AdminWarningToast.Visibility = Visibility.Collapsed;

        private void RestartToUpdate_Click(object sender, RoutedEventArgs e)
            => ViewModel.RestartToUpdateCommand.Execute(null);

        private void UpdateErrorClose_Click(object sender, RoutedEventArgs e)
            => ViewModel.IsUpdateErrorVisible = false;

        private void UpdateNoUpdateClose_Click(object sender, RoutedEventArgs e)
            => ViewModel.IsUpdateNoUpdateVisible = false;

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.IsMediaOverlayOpen) && !ViewModel.IsMediaOverlayOpen)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => StopAllMediaPlayers(MediaOverlayFlipView));

            if (e.PropertyName == nameof(ViewModel.IsInitialLoading) && !ViewModel.IsInitialLoading)
                _libraryReadyForStartup.TrySetResult(true);

            if (e.PropertyName == nameof(ViewModel.IsOnboardingVisible)
                && ViewModel.IsOnboardingVisible
                && StartupOverlay.Visibility == Visibility.Collapsed)
            {
                DispatcherQueue.TryEnqueue(PlayOnboardingEntranceAnimation);
            }

            if (e.PropertyName == nameof(ViewModel.IsSidebarCollapsed))
                ApplySidebarState(ViewModel.IsSidebarCollapsed, animate: true);
        }

        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            bool controlDown = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            if (controlDown && (e.Key == VirtualKey.F || e.Key == VirtualKey.S))
            {
                if (CanFocusSidebarSearch())
                {
                    FocusSidebarSearch();
                    e.Handled = true;
                }
                return;
            }

            if (e.Key != VirtualKey.Back || IsTextInputSource(e.OriginalSource as DependencyObject))
                return;

            if (HandleDetailBackRequest())
                e.Handled = true;
        }

        private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (!IsWithinDetailSurface(e.OriginalSource as DependencyObject))
                return;

            if (HandleDetailBackRequest())
                e.Handled = true;
        }

        private bool CanFocusSidebarSearch() =>
            ViewModel.IsUiEnabled
            && !ViewModel.IsInitialLoading
            && !ViewModel.IsLoadingVisible
            && !ViewModel.IsOnboardingVisible
            && !ViewModel.IsSettingsVisible
            && !ViewModel.IsResetConfirmVisible
            && !ViewModel.IsGameSettingsOpen
            && !ViewModel.IsMediaOverlayOpen
            && !ViewModel.IsFranchiseOverlayOpen;

        private void FocusSidebarSearch()
        {
            if (ViewModel.IsSidebarCollapsed)
                ViewModel.IsSidebarCollapsed = false;

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                SidebarSearchTextBox.Focus(FocusState.Keyboard);
                SidebarSearchTextBox.SelectAll();
            });
        }

        private bool HandleDetailBackRequest()
        {
            if (!ViewModel.IsDetailsVisible
                || ViewModel.IsInitialLoading
                || ViewModel.IsLoadingVisible
                || ViewModel.IsOnboardingVisible
                || ViewModel.IsSettingsVisible
                || ViewModel.IsResetConfirmVisible)
            {
                return false;
            }

            if (GameDetailHost.TryCloseTransientLayer())
                return true;

            if (ViewModel.IsMediaOverlayOpen)
            {
                ViewModel.CloseMediaOverlayCommand.Execute(null);
                return true;
            }

            if (ViewModel.IsFranchiseOverlayOpen)
            {
                ViewModel.CloseFranchiseOverlayCommand.Execute(null);
                return true;
            }

            if (ViewModel.BackCommand.CanExecute(null))
                ViewModel.BackCommand.Execute(null);
            return true;
        }

        private static bool IsTextInputSource(DependencyObject? source)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is TextBox or PasswordBox or RichEditBox)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private bool IsWithinDetailSurface(DependencyObject? source)
        {
            if (source == null)
                return false;

            return IsDescendantOf(source, GameDetailHost)
                || (ViewModel.IsMediaOverlayOpen && IsDescendantOf(source, MediaOverlay))
                || (ViewModel.IsFranchiseOverlayOpen && IsDescendantOf(source, FranchiseOverlay));
        }

        private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private void ApplySidebarState(bool isCollapsed, bool animate)
        {
            _sidebarStateStoryboard?.Stop();
            _sidebarStateStoryboard = null;

            bool animationsEnabled = animate;
            try
            {
                animationsEnabled &= new UISettings().AnimationsEnabled;
            }
            catch
            {
                animationsEnabled = false;
            }

            if (!animationsEnabled)
            {
                SidebarColumn.Width = isCollapsed ? new GridLength(0) : new GridLength(220);
                SidebarBorder.Visibility = isCollapsed ? Visibility.Collapsed : Visibility.Visible;
                SidebarBorder.Opacity = 1;
                SidebarTranslateTransform.X = 0;
                return;
            }

            if (!isCollapsed)
            {
                SidebarColumn.Width = new GridLength(220);
                SidebarBorder.Visibility = Visibility.Visible;
                SidebarBorder.Opacity = 0;
                SidebarTranslateTransform.X = -18;
            }

            var duration = new Duration(TimeSpan.FromMilliseconds(150));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var opacity = new DoubleAnimation
            {
                To = isCollapsed ? 0 : 1,
                Duration = duration,
                EasingFunction = ease
            };
            var translate = new DoubleAnimation
            {
                To = isCollapsed ? -18 : 0,
                Duration = duration,
                EasingFunction = ease,
                EnableDependentAnimation = true
            };

            Storyboard.SetTarget(opacity, SidebarBorder);
            Storyboard.SetTargetProperty(opacity, "Opacity");
            Storyboard.SetTarget(translate, SidebarTranslateTransform);
            Storyboard.SetTargetProperty(translate, "X");

            var storyboard = new Storyboard();
            storyboard.Children.Add(opacity);
            storyboard.Children.Add(translate);
            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_sidebarStateStoryboard, storyboard))
                    return;

                if (isCollapsed)
                {
                    SidebarBorder.Visibility = Visibility.Collapsed;
                    SidebarColumn.Width = new GridLength(0);
                }
                SidebarBorder.Opacity = 1;
                SidebarTranslateTransform.X = 0;
                _sidebarStateStoryboard = null;
            };
            _sidebarStateStoryboard = storyboard;
            storyboard.Begin();
        }

        private async Task PlayStartupOpenAnimationAsync()
        {
            if (_startupSequenceStarted)
                return;

            _startupSequenceStarted = true;
            bool animationsEnabled = new UISettings().AnimationsEnabled;
            FrameworkElement[] tiles =
            [
                StartupTile0, StartupTile1, StartupTile2,
                StartupTile3, StartupTile4, StartupTile5,
                StartupTile6, StartupTile7, StartupTile8
            ];

            if (!animationsEnabled)
            {
                foreach (FrameworkElement tile in tiles)
                    tile.Opacity = 0.42;

                StartupBrand.Opacity = 1;
                await FinishStartupAsync(450, 180);
                return;
            }

            var intro = new Storyboard();
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

            for (int index = 0; index < tiles.Length; index++)
            {
                var transform = (CompositeTransform)tiles[index].RenderTransform;
                transform.TranslateY = 18;

                var fade = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    BeginTime = TimeSpan.FromMilliseconds(120 + (index * 70)),
                    Duration = TimeSpan.FromMilliseconds(360),
                    EasingFunction = easeOut
                };
                Storyboard.SetTarget(fade, tiles[index]);
                Storyboard.SetTargetProperty(fade, "Opacity");
                intro.Children.Add(fade);

                var rise = new DoubleAnimation
                {
                    From = 18,
                    To = 0,
                    BeginTime = fade.BeginTime,
                    Duration = fade.Duration,
                    EasingFunction = easeOut,
                    EnableDependentAnimation = true
                };
                Storyboard.SetTarget(rise, transform);
                Storyboard.SetTargetProperty(rise, "TranslateY");
                intro.Children.Add(rise);
            }

            var scanOpacity = new DoubleAnimationUsingKeyFrames();
            scanOpacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), Value = 0 });
            scanOpacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), Value = 0.9 });
            scanOpacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1320)), Value = 0 });
            Storyboard.SetTarget(scanOpacity, StartupScanLine);
            Storyboard.SetTargetProperty(scanOpacity, "Opacity");
            intro.Children.Add(scanOpacity);

            var scanMove = new DoubleAnimation
            {
                From = -170,
                To = 170,
                BeginTime = TimeSpan.FromMilliseconds(450),
                Duration = TimeSpan.FromMilliseconds(870),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(scanMove, StartupScanTransform);
            Storyboard.SetTargetProperty(scanMove, "TranslateY");
            intro.Children.Add(scanMove);

            foreach (string property in new[] { "ScaleX", "ScaleY" })
            {
                var contract = new DoubleAnimation
                {
                    From = 1,
                    To = 0.84,
                    BeginTime = TimeSpan.FromMilliseconds(1350),
                    Duration = TimeSpan.FromMilliseconds(520),
                    EasingFunction = easeOut
                };
                Storyboard.SetTarget(contract, StartupMosaicTransform);
                Storyboard.SetTargetProperty(contract, property);
                intro.Children.Add(contract);
            }

            var soften = new DoubleAnimation
            {
                From = 1,
                To = 0.28,
                BeginTime = TimeSpan.FromMilliseconds(1450),
                Duration = TimeSpan.FromMilliseconds(430)
            };
            Storyboard.SetTarget(soften, StartupMosaic);
            Storyboard.SetTargetProperty(soften, "Opacity");
            intro.Children.Add(soften);

            var showBrand = new DoubleAnimation
            {
                From = 0,
                To = 1,
                BeginTime = TimeSpan.FromMilliseconds(1480),
                Duration = TimeSpan.FromMilliseconds(420),
                EasingFunction = easeOut
            };
            Storyboard.SetTarget(showBrand, StartupBrand);
            Storyboard.SetTargetProperty(showBrand, "Opacity");
            intro.Children.Add(showBrand);

            intro.Begin();
            await FinishStartupAsync(2200, 300);
        }

        private async Task FinishStartupAsync(int minimumDurationMs, int fadeDurationMs)
        {
            await Task.WhenAll(
                Task.Delay(minimumDurationMs),
                _libraryReadyForStartup.Task);

            await FadeStartupOverlayAsync(fadeDurationMs);
            _ = ViewModel.StartBackgroundMaintenanceAsync();
        }

        private Task FadeStartupOverlayAsync(int durationMs)
        {
            var completion = new TaskCompletionSource<bool>();
            var transform = (CompositeTransform)StartupOverlay.RenderTransform;
            transform.CenterX = StartupOverlay.ActualWidth / 2;
            transform.CenterY = StartupOverlay.ActualHeight / 2;
            var duration = TimeSpan.FromMilliseconds(durationMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var storyboard = new Storyboard();

            var fade = new DoubleAnimation { From = StartupOverlay.Opacity, To = 0, Duration = duration, EasingFunction = ease };
            Storyboard.SetTarget(fade, StartupOverlay);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);

            foreach (string property in new[] { "ScaleX", "ScaleY" })
            {
                var scale = new DoubleAnimation { From = 1, To = 1.035, Duration = duration, EasingFunction = ease };
                Storyboard.SetTarget(scale, transform);
                Storyboard.SetTargetProperty(scale, property);
                storyboard.Children.Add(scale);
            }

            storyboard.Completed += (_, _) =>
            {
                StartupOverlay.Visibility = Visibility.Collapsed;
                if (ViewModel.IsOnboardingVisible)
                    PlayOnboardingEntranceAnimation();
                completion.TrySetResult(true);
            };
            storyboard.Begin();
            return completion.Task;
        }

        private void PlayOnboardingEntranceAnimation()
        {
            if (!new UISettings().AnimationsEnabled)
            {
                OnboardingMascot.Opacity = 1;
                OnboardingCopy.Opacity = 1;
                return;
            }

            var storyboard = new Storyboard();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            FrameworkElement[] elements = [OnboardingMascot, OnboardingCopy];

            for (int index = 0; index < elements.Length; index++)
            {
                FrameworkElement element = elements[index];
                var transform = (CompositeTransform)element.RenderTransform;
                transform.TranslateY = index == 0 ? 10 : 16;
                element.Opacity = 0;

                var fade = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    BeginTime = TimeSpan.FromMilliseconds(index * 90),
                    Duration = TimeSpan.FromMilliseconds(420),
                    EasingFunction = ease
                };
                Storyboard.SetTarget(fade, element);
                Storyboard.SetTargetProperty(fade, "Opacity");
                storyboard.Children.Add(fade);

                var rise = new DoubleAnimation
                {
                    From = transform.TranslateY,
                    To = 0,
                    BeginTime = fade.BeginTime,
                    Duration = fade.Duration,
                    EasingFunction = ease,
                    EnableDependentAnimation = true
                };
                Storyboard.SetTarget(rise, transform);
                Storyboard.SetTargetProperty(rise, "TranslateY");
                storyboard.Children.Add(rise);
            }

            storyboard.Begin();
        }

        private static void StopAllMediaPlayers(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is MediaPlayerElement mediaPlayerElement)
                    mediaPlayerElement.MediaPlayer?.Pause();
                else
                    StopAllMediaPlayers(child);
            }
        }

        private void ConfigureWindowConstraints()
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = MinWindowWidth;
                presenter.PreferredMinimumHeight = MinWindowHeight;
            }

            if (AppWindow.ClientSize.Width < MinWindowWidth || AppWindow.ClientSize.Height < MinWindowHeight)
            {
                AppWindow.ResizeClient(new SizeInt32(
                    Math.Max(AppWindow.ClientSize.Width, MinWindowWidth),
                    Math.Max(AppWindow.ClientSize.Height, MinWindowHeight)));
            }
        }

        private void SidebarGameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListView list)
                return;

            UpdateSidebarGameListForegrounds(list);

            if (list.SelectedItem is Game game)
                ViewModel.SelectGameCommand.Execute(game);
        }

        private void SidebarGameList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer is ListViewItem container)
                ApplySidebarItemForeground(container, ReferenceEquals(args.Item, sender.SelectedItem));
        }

        private void MediaScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (ViewModel.CloseMediaOverlayCommand.CanExecute(null))
                ViewModel.CloseMediaOverlayCommand.Execute(null);
        }

        private void FranchiseScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => ViewModel.CloseFranchiseOverlayCommand.Execute(null);

        private void LibraryTitle_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.BackCommand.CanExecute(null))
                ViewModel.BackCommand.Execute(null);
        }

        private static void ApplySidebarItemForeground(ListViewItem container, bool isSelected)
        {
            var brush = isSelected ? SidebarSelectedForegroundBrush : SidebarUnselectedForegroundBrush;
            container.Foreground = brush;

            if (container.ContentTemplateRoot is TextBlock textBlock)
                textBlock.Foreground = brush;
        }

        private static void UpdateSidebarGameListForegrounds(ListView list)
        {
            foreach (var item in list.Items)
            {
                if (list.ContainerFromItem(item) is ListViewItem container)
                    ApplySidebarItemForeground(container, ReferenceEquals(item, list.SelectedItem));
            }
        }

        private void SidebarSettingsButton_Click(object sender, RoutedEventArgs e)
            => ViewModel.IsSettingsVisible = true;

        private async void SidebarScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.ScanGamesCommand.CanExecute(null))
                return;

            ViewModel.IsSettingsVisible = false;
            await ViewModel.ScanGamesCommand.ExecuteAsync(null);
        }

        private async void SidebarAddExecutableButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsSettingsVisible = false;
            try
            {
                var path = await PickExeFileAsync();
                if (path == null)
                    return;

                await ViewModel.AddGameCommand(path);
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "Add by Executable Error",
                    Content = ex.Message,
                    CloseButtonText = "Close",
                    XamlRoot = Content.XamlRoot
                };
                await errorDialog.ShowAsync();
            }
        }

        private void SettingsClose_Click(object sender, RoutedEventArgs e)
            => ViewModel.IsSettingsVisible = false;

        private void SettingsScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => ViewModel.IsSettingsVisible = false;

        private async void SteamDisconnect_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Disconnect Steam",
                Content = "Your Steam games will be hidden until you connect again. Installed games stay visible.",
                PrimaryButtonText = "Disconnect",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                await ViewModel.DisconnectSteamAsync();
        }

        private async void DebugCheckIds_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsUiEnabled = false;
            try
            {
                var path = await PickExeFileAsync();
                if (path == null)
                    return;

                Debug.WriteLine($"Starting ID lookup for: {path}");
                var (steamId, steamName) = await App.Services.GameName.FindGameIdsAsync(path);

                string steamIdText = steamId.HasValue ? steamId.Value.ToString() : "Not found";
                string bestName = steamName ?? App.Services.GameName.GetBestName(path) ?? "Unknown";

                var testDialog = new ContentDialog
                {
                    Title = "GameName and ID Service",
                    Content = $"Name: {bestName}\nSteam ID: {steamIdText}",
                    CloseButtonText = "Ok",
                    XamlRoot = Content.XamlRoot
                };
                await testDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "Check IDs Error",
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

        private async void DebugLaunchSteamSilent_Click(object sender, RoutedEventArgs e)
        {
            string? error = ViewModel.TryLaunchSteamSilent();
            var dialog = new ContentDialog
            {
                Title = "Launch Steam Silent",
                Content = error ?? "Steam launched with -silent flag.",
                CloseButtonText = "Ok",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async void Debug_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsUiEnabled = false;
            try
            {
                var panel = new StackPanel { Spacing = 8, MinWidth = 900 };

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
                    AddRow("Epic ID", g.EpicAppId);
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
                    AddRow("Original Release Date", g.OriginalReleaseDate?.ToString());
                    AddRow("Original Game Name", g.OriginalGameName);
                    AddRow("IGDB Version Parent ID", g.IgdbVersionParentId?.ToString());
                    AddRow("IGDB Category", g.IgdbCategory?.ToString());
                    AddRow("IGDB Category Name", g.IgdbCategoryName);
                    AddRow("Is Remake/Remaster", g.IsRemakeOrRemaster.ToString());
                    AddRow("Steam Rating", g.SteamRating?.ToString());
                    AddRow("Steam Review Summary", g.SteamReviewSummary);
                    AddRow("Steam Review Total", g.SteamReviewTotal?.ToString());
                    AddRow("Age Rating", g.AgeRating);
                    AddRow("Main Story (sec)", g.TimeToCompleteMainStory?.ToString());
                    AddRow("Completionist (sec)", g.TimeToCompleteCompletionist?.ToString());
                    AddRow("Capsule", g.LibraryCapsule);
                    AddRow("Hero", g.LibraryHero);
                    AddRow("Logo", g.LibraryLogo);
                    AddRow("Media", FormatList(g.Media));
                    AddRow("Official Website", g.OfficialWebsiteUrl);
                    AddRow("Steam Page", g.SteamPageUrl);
                    AddRow("RAWG Page", g.RawgUrl);
                    AddRow("IGDB Page", g.IgdbUrl);

                    expander.Content = grid;
                    panel.Children.Add(expander);
                }

                var container = new StackPanel { Spacing = 8, MaxWidth = 1100 };
                container.Children.Add(new TextBlock
                {
                    Text = $"{ViewModel.Games.Count} games in library",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 14
                });
                container.Children.Add(new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = 540,
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

        private void ResetApp_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsSettingsVisible = false;
            ViewModel.IsResetConfirmVisible = true;
        }

        private void ResetScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            ViewModel.IsResetConfirmVisible = false;
        }

        private void ResetCancel_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsResetConfirmVisible = false;
        }

        private async void ResetConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (_isResetting)
                return;

            _isResetting = true;
            ViewModel.IsResetConfirmVisible = false;
            ViewModel.IsUiEnabled = false;
            NotificationStack.Visibility = Visibility.Collapsed;
            ResetProgressOverlay.Visibility = Visibility.Visible;
            await Task.Delay(3000);

            try
            {
                App.Services.AppReset.StartFullResetAndRestart();
            }
            catch (Exception ex)
            {
                _isResetting = false;
                ResetProgressOverlay.Visibility = Visibility.Collapsed;
                NotificationStack.Visibility = Visibility.Visible;
                var errorDialog = new ContentDialog
                {
                    Title = "Reset Error",
                    Content = ex.Message,
                    CloseButtonText = "Close",
                    XamlRoot = Content.XamlRoot
                };
                await errorDialog.ShowAsync();
                ViewModel.IsUiEnabled = true;
            }
        }

        private async Task<string?> PickExeFileAsync()
        {
            using var identity = WindowsIdentity.GetCurrent();
            bool isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

            if (isAdmin)
                return PickExeFileWin32(WindowNative.GetWindowHandle(this));

            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }

        private static string? PickExeFileWin32(IntPtr hwndOwner)
        {
            var ofn = new OpenFileName
            {
                hwndOwner = hwndOwner,
                lpstrFilter = "Executable Files\0*.exe\0All Files\0*.*\0",
                lpstrFile = new string('\0', 260),
                nMaxFile = 260,
                lpstrTitle = "Select Executable",
                Flags = 0x00001000 | 0x00000800 | 0x00000008 // OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
            };
            ofn.lStructSize = Marshal.SizeOf(ofn);

            return GetOpenFileName(ref ofn) ? ofn.lpstrFile.TrimEnd('\0') : null;
        }

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetOpenFileName(ref OpenFileName ofn);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string? lpstrFilter;
            public string? lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public string? lpstrFile;
            public int nMaxFile;
            public string? lpstrFileTitle;
            public int nMaxFileTitle;
            public string? lpstrInitialDir;
            public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string? lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string? lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int flagsEx;
        }
    }
}
