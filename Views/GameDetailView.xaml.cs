using Codec.Helpers;
using Codec.Models;
using Codec.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Views
{
    public sealed partial class GameDetailView : UserControl
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileName(ref OPENFILENAME lpofn);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string? lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
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
            public int FlagsEx;
        }

        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_NOCHANGEDIR = 0x00000008;

        private readonly DispatcherQueue _dispatcherQueue;
        private CancellationTokenSource? _heroPaletteCts;
        private MainViewModel? _observedViewModel;
        private Game? _observedGame;
        private long _heroPaletteVersion;

        private MainViewModel? ViewModel => DataContext as MainViewModel;

        public GameDetailView()
        {
            InitializeComponent();

            _dispatcherQueue = DispatcherQueue.GetForCurrentThread()!;
            ApplyHeroActionPalette(HeroActionPaletteExtractor.Default);

            DataContextChanged += GameDetailView_DataContextChanged;
            Loaded += GameDetailView_Loaded;
            Unloaded += GameDetailView_Unloaded;
            SizeChanged += GameDetailView_SizeChanged;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
            => ViewModel?.BackCommand.Execute(null);

        private void Scrim_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (ViewModel?.CloseGameSettingsCommand.CanExecute(null) == true)
                ViewModel.CloseGameSettingsCommand.Execute(null);
        }

        private async void SelectScriptButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.SelectedGame == null)
                return;

            string? gameFolderPath = ViewModel.SelectedGame.FolderLocation;
            string? selectedFilePath = OpenFileDialog(gameFolderPath);

            if (!string.IsNullOrWhiteSpace(selectedFilePath))
            {
                if (ViewModel.SetLaunchScriptCommand.CanExecute(selectedFilePath))
                    await ViewModel.SetLaunchScriptCommand.ExecuteAsync(selectedFilePath);
            }
        }

        private string? OpenFileDialog(string? initialDirectory)
        {
            var fileBuffer = new char[260];
            var fileBufferPtr = Marshal.AllocHGlobal(260 * 2);
            Marshal.Copy(fileBuffer, 0, fileBufferPtr, fileBuffer.Length);

            try
            {
                var ofn = new OPENFILENAME
                {
                    lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                    hwndOwner = GetActiveWindow(),
                    lpstrFilter = "Batch Files (*.bat)\0*.bat\0All Files (*.*)\0*.*\0\0",
                    nFilterIndex = 1,
                    lpstrFile = fileBufferPtr,
                    nMaxFile = 260,
                    lpstrInitialDir = !string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)
                        ? initialDirectory
                        : null,
                    lpstrTitle = "Select Launch Script",
                    Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
                };

                if (GetOpenFileName(ref ofn))
                {
                    return Marshal.PtrToStringUni(fileBufferPtr);
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(fileBufferPtr);
            }
        }

        private async void DeleteSelectedGameButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.SelectedGame == null)
                return;

            var dialog = new ContentDialog
            {
                Title = "Delete Game",
                Content = $"Remove '{ViewModel.SelectedGame.Name}' from your library? This won't delete any files from disk.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            if (ViewModel.DeleteSelectedGameCommand.CanExecute(null))
                await ViewModel.DeleteSelectedGameCommand.ExecuteAsync(null);
        }

        private void GameDetailView_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            AttachToViewModel(args.NewValue as MainViewModel);
            QueueHeroActionPaletteRefresh();
        }

        private void GameDetailView_Loaded(object sender, RoutedEventArgs e)
        {
            AttachToViewModel(ViewModel);
            UpdateSettingsLayoutState();
            QueueHeroActionPaletteRefresh();
        }

        private void GameDetailView_Unloaded(object sender, RoutedEventArgs e)
        {
            CancelHeroActionPaletteRefresh();
            DetachFromGame();
            DetachFromViewModel();
        }

        private void GameDetailView_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateSettingsLayoutState();

        private void UpdateSettingsLayoutState()
        {
            if (RootLayout == null)
                return;

            double width = ActualWidth;
            double height = ActualHeight;

            string stateName = "SettingsComfortableState";

            if (width <= 1100 || height <= 690)
            {
                stateName = "SettingsTightState";
            }
            else if (width <= 1320 || height <= 820)
            {
                stateName = "SettingsCompactState";
            }

            _ = VisualStateManager.GoToState(this, stateName, useTransitions: true);
        }

        private void HeroOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double padding = Math.Clamp(e.NewSize.Width * 0.03, 20, 64);
            if (HeroLogo != null)
            {
                HeroLogo.Margin = new Thickness(padding, padding, 0, 0);
                double targetHeight = Math.Clamp(e.NewSize.Width * 0.18, 170, 320);
                HeroLogo.Height = targetHeight;
                HeroLogo.MaxWidth = Math.Clamp(targetHeight * 2.2, 360, 640);
            }
        }

        private void AttachToViewModel(MainViewModel? viewModel)
        {
            if (ReferenceEquals(_observedViewModel, viewModel))
            {
                return;
            }

            DetachFromViewModel();
            _observedViewModel = viewModel;

            if (_observedViewModel != null)
            {
                _observedViewModel.PropertyChanged += ObservedViewModel_PropertyChanged;
            }

            AttachToGame(viewModel?.SelectedGame);
        }

        private void DetachFromViewModel()
        {
            if (_observedViewModel != null)
            {
                _observedViewModel.PropertyChanged -= ObservedViewModel_PropertyChanged;
                _observedViewModel = null;
            }
        }

        private void AttachToGame(Game? game)
        {
            if (ReferenceEquals(_observedGame, game))
            {
                return;
            }

            DetachFromGame();
            _observedGame = game;

            if (_observedGame != null)
            {
                _observedGame.PropertyChanged += ObservedGame_PropertyChanged;
            }
        }

        private void DetachFromGame()
        {
            if (_observedGame != null)
            {
                _observedGame.PropertyChanged -= ObservedGame_PropertyChanged;
                _observedGame = null;
            }
        }

        private void ObservedViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != nameof(MainViewModel.SelectedGame))
            {
                return;
            }

            AttachToGame(_observedViewModel?.SelectedGame);
            QueueHeroActionPaletteRefresh();
        }

        private void ObservedGame_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName)
                && e.PropertyName != nameof(Game.LibHero)
                && e.PropertyName != nameof(Game.LibHeroCache)
                && e.PropertyName != nameof(Game.LibHeroUrl))
            {
                return;
            }

            QueueHeroActionPaletteRefresh();
        }

        private void QueueHeroActionPaletteRefresh()
        {
            CancelHeroActionPaletteRefresh();

            long version = Interlocked.Increment(ref _heroPaletteVersion);
            var cts = new CancellationTokenSource();
            _heroPaletteCts = cts;

            _ = RefreshHeroActionPaletteAsync(_observedGame?.LibHeroCache, _observedGame?.LibHero, version, cts.Token);
        }

        private void CancelHeroActionPaletteRefresh()
        {
            _heroPaletteCts?.Cancel();
            _heroPaletteCts?.Dispose();
            _heroPaletteCts = null;
        }

        private async Task RefreshHeroActionPaletteAsync(string? heroCachePath, string? heroPath, long version, CancellationToken cancellationToken)
        {
            HeroActionPalette palette;

            try
            {
                palette = await HeroActionPaletteExtractor.ExtractAsync(heroCachePath, heroPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested || version != Interlocked.Read(ref _heroPaletteVersion))
            {
                return;
            }

            void ApplyCurrentPalette()
            {
                if (cancellationToken.IsCancellationRequested || version != Interlocked.Read(ref _heroPaletteVersion))
                {
                    return;
                }

                ApplyHeroActionPalette(palette);
            }

            if (_dispatcherQueue.HasThreadAccess)
            {
                ApplyCurrentPalette();
            }
            else
            {
                _ = _dispatcherQueue.TryEnqueue(ApplyCurrentPalette);
            }
        }

        private void ApplyHeroActionPalette(HeroActionPalette palette)
        {
            DetailsPlayShell.Background = new SolidColorBrush(palette.PlayBackgroundColor);
            DetailsPlayShell.BorderBrush = CreateBorderBrush(
                palette.PlayBorderStartColor,
                palette.PlayBorderMidColor,
                palette.PlayBorderEndColor,
                0.42);

            DetailsSettingsShell.Background = new SolidColorBrush(palette.SettingsBackgroundColor);
            DetailsSettingsShell.BorderBrush = CreateBorderBrush(
                palette.SettingsBorderStartColor,
                palette.SettingsBorderMidColor,
                palette.SettingsBorderEndColor,
                0.52);

            var playForeground = new SolidColorBrush(palette.ForegroundColor);
            DetailsPlayGlyph.Foreground = playForeground;
            DetailsPlayText.Foreground = playForeground;
            DetailsSettingsGlyph.Foreground = new SolidColorBrush(palette.MutedForegroundColor);
        }

        private static LinearGradientBrush CreateBorderBrush(Windows.UI.Color startColor, Windows.UI.Color midColor, Windows.UI.Color endColor, double midOffset)
            => new()
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = startColor, Offset = 0 },
                    new GradientStop { Color = midColor, Offset = midOffset },
                    new GradientStop { Color = endColor, Offset = 1 }
                }
            };
    }
}
