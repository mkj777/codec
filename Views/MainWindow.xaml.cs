using Codec.Models;
using Codec.Services;
using Codec.Services.Storage;
using Codec.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
using WinRT.Interop;

namespace Codec.Views
{
    public sealed partial class MainWindow : Window
    {
        private const int MinWindowWidth = 900;
        private const int MinWindowHeight = 560;

        private static readonly SolidColorBrush SidebarSelectedForegroundBrush = new(Colors.White);
        private static readonly SolidColorBrush SidebarUnselectedForegroundBrush = new(ColorHelper.FromArgb(0xFF, 0x9A, 0x9A, 0x9A));

        private static readonly Windows.UI.Color[] FireColors =
        [
            ColorHelper.FromArgb(255, 255, 220, 30),
            ColorHelper.FromArgb(255, 255, 170, 0),
            ColorHelper.FromArgb(255, 255, 100, 0),
            ColorHelper.FromArgb(255, 255, 50, 0),
            ColorHelper.FromArgb(255, 220, 20, 0),
        ];

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
            _ = ViewModel.LoadLibraryAsync();
            RootGrid.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
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
                DispatcherQueue.TryEnqueue(() => PlayStartupOpenAnimation());
        }

        private void PlayStartupOpenAnimation()
        {
            var transform = (CompositeTransform)StartupOverlay.RenderTransform;
            transform.CenterX = StartupOverlay.ActualWidth / 2;
            transform.CenterY = StartupOverlay.ActualHeight / 2;

            var duration = new Duration(TimeSpan.FromMilliseconds(280));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var fadeOut = new DoubleAnimation { From = 1, To = 0, Duration = duration, EasingFunction = ease };
            var scaleX = new DoubleAnimation { From = 1, To = 1.04, Duration = duration, EasingFunction = ease };
            var scaleY = new DoubleAnimation { From = 1, To = 1.04, Duration = duration, EasingFunction = ease };

            Storyboard.SetTarget(fadeOut, StartupOverlay);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            Storyboard.SetTarget(scaleX, transform);
            Storyboard.SetTargetProperty(scaleX, "ScaleX");
            Storyboard.SetTarget(scaleY, transform);
            Storyboard.SetTargetProperty(scaleY, "ScaleY");

            var sb = new Storyboard();
            sb.Children.Add(fadeOut);
            sb.Children.Add(scaleX);
            sb.Children.Add(scaleY);
            sb.Completed += (_, _) => StartupOverlay.Visibility = Visibility.Collapsed;
            sb.Begin();
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

        private void ManageButton_Click(object sender, RoutedEventArgs e)
            => ViewModel.IsSettingsVisible = true;

        private void SettingsClose_Click(object sender, RoutedEventArgs e)
            => ViewModel.IsSettingsVisible = false;

        private void SettingsScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => ViewModel.IsSettingsVisible = false;

        private async void SidebarAddGames_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsSettingsVisible = false;
            await ViewModel.ScanGamesCommand.ExecuteAsync(null);
        }

        private async void OnboardingStart_Click(object sender, RoutedEventArgs e)
        {
            bool scanOnStartup = OnboardingScanOnStartupToggle.IsOn;
            bool launchSteamSilent = OnboardingLaunchSteamSilentToggle.IsOn;
            ViewModel.IsOnboardingVisible = false;
            await ViewModel.CompleteOnboardingAsync(scanOnStartup, launchSteamSilent);
            await ViewModel.ScanGamesCommand.ExecuteAsync(null);
        }

        private async void OnboardingSkip_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.CompleteOnboardingAsync(false, false);
            ViewModel.IsOnboardingVisible = false;
        }

        private async void AddGame_Click(object sender, RoutedEventArgs e)
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
                    AddRow("Capsule", g.LibCapsule);
                    AddRow("Hero", g.LibHero);
                    AddRow("Logo", g.LibLogo);
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

        private async void ResetApp_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsSettingsVisible = false;
            var confirmationDialog = new ContentDialog
            {
                Title = "Reset Codec",
                Content = "This will delete all saved data and cached files. Continue?",
                PrimaryButtonText = "Delete Data",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            if (await confirmationDialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            ViewModel.IsUiEnabled = false;
            ViewModel.CancelImport();
            ViewModel.IsDetailsVisible = false;
            ViewModel.SelectedGame = null;
            ViewModel.SidebarSelectedItem = null;
            ViewModel.Games.Clear();

            var fireTask = PlayFireAnimationAsync();

            try
            {
                App.Services.Cache.ClearAll();
                await App.Services.LibraryStorage.ResetAsync();
            }
            catch (Exception ex)
            {
                await fireTask;
                var errorDialog = new ContentDialog
                {
                    Title = "Reset Error",
                    Content = ex.Message,
                    CloseButtonText = "Close",
                    XamlRoot = Content.XamlRoot
                };
                await errorDialog.ShowAsync();
                ViewModel.IsUiEnabled = true;
                return;
            }

            ViewModel.ResetAppSettings();
            ViewModel.SetLoadingState(false);

            await fireTask;

            ViewModel.IsOnboardingVisible = true;
            ViewModel.IsUiEnabled = true;
        }

        private async Task PlayFireAnimationAsync()
        {
            FireOverlay.Visibility = Visibility.Visible;
            FireDarkBackground.Opacity = 1;

            AnimateOpacity(FireBottomGlow, 0, 1.0, 0.5);

            var shakeSb = BuildShakeStoryboard();
            shakeSb.Begin();

            var random = new Random();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) =>
            {
                SpawnFireParticle(FireParticleCanvas, random);
                SpawnFireParticle(FireParticleCanvas, random);
                SpawnFireParticle(FireParticleCanvas, random);
            };
            timer.Start();

            await Task.Delay(2600);
            timer.Stop();

            AnimateOpacity(FireOverlay, 1, 0, 0.65);
            await Task.Delay(650);

            shakeSb.Stop();
            ShakeTransform.X = 0;
            FireOverlay.Visibility = Visibility.Collapsed;
            FireOverlay.Opacity = 1;
            FireDarkBackground.Opacity = 0;
            FireBottomGlow.Opacity = 0;
            FireParticleCanvas.Children.Clear();
        }

        private void SpawnFireParticle(Microsoft.UI.Xaml.Controls.Canvas canvas, Random random)
        {
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var color = FireColors[random.Next(FireColors.Length)];
            double size = 14 + random.NextDouble() * 38;
            double x = random.NextDouble() * (w + size) - size / 2;
            double secs = 0.7 + random.NextDouble() * 1.1;

            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop { Color = color, Offset = 0 });
            brush.GradientStops.Add(new GradientStop
            {
                Color = ColorHelper.FromArgb(0, color.R, color.G, color.B),
                Offset = 1
            });

            var ellipse = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = size,
                Height = size * 1.7,
                Fill = brush,
                Opacity = 0.7 + random.NextDouble() * 0.3,
                IsHitTestVisible = false
            };

            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(ellipse, x);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(ellipse, h);
            canvas.Children.Add(ellipse);

            var move = new DoubleAnimation
            {
                From = h,
                To = -size * 2,
                Duration = new Duration(TimeSpan.FromSeconds(secs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fade = new DoubleAnimation
            {
                From = ellipse.Opacity,
                To = 0,
                BeginTime = TimeSpan.FromSeconds(secs * 0.5),
                Duration = new Duration(TimeSpan.FromSeconds(secs * 0.5))
            };

            var sb = new Storyboard();
            Storyboard.SetTarget(move, ellipse);
            Storyboard.SetTargetProperty(move, "(Canvas.Top)");
            Storyboard.SetTarget(fade, ellipse);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(move);
            sb.Children.Add(fade);
            sb.Completed += (_, _) => canvas.Children.Remove(ellipse);
            sb.Begin();
        }

        private Storyboard BuildShakeStoryboard()
        {
            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = new RepeatBehavior(11) };
            (double X, double Ms)[] keys =
            [
                (0, 0), (8, 38), (-8, 76), (5, 111), (-5, 146), (3, 176), (-3, 206), (0, 236)
            ];
            foreach (var (x, ms) in keys)
                anim.KeyFrames.Add(new LinearDoubleKeyFrame
                {
                    Value = x,
                    KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))
                });

            Storyboard.SetTarget(anim, ShakeTransform);
            Storyboard.SetTargetProperty(anim, "X");

            var sb = new Storyboard();
            sb.Children.Add(anim);
            return sb;
        }

        private static void AnimateOpacity(UIElement target, double from, double to, double secs)
        {
            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromSeconds(secs))
            };
            var sb = new Storyboard();
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
            sb.Begin();
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
