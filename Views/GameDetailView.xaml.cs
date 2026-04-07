using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Runtime.InteropServices;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Codec.Views
{
    public sealed partial class GameDetailView : UserControl
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();


        private MainViewModel? ViewModel => DataContext as MainViewModel;
        private double _mediaMaxHeight;

        public GameDetailView()
        {
            InitializeComponent();
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

            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add(".bat");

            var hwnd = GetActiveWindow();
            if (hwnd == IntPtr.Zero)
                return;

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            // Use game-specific settings identifier so picker remembers folder for this game
            if (!string.IsNullOrWhiteSpace(ViewModel.SelectedGame.FolderLocation))
            {
                try
                {
                    var gameFolder = await StorageFolder.GetFolderFromPathAsync(ViewModel.SelectedGame.FolderLocation);
                    if (gameFolder != null)
                    {
                        // Use game name as part of settings ID so each game remembers its browse location
                        string gameId = ViewModel.SelectedGame.Name?.Replace(" ", "_") ?? "game";
                        picker.SettingsIdentifier = $"GameScript_{gameId}";
                    }
                }
                catch
                {
                    // Fall back to default if folder access fails
                }
            }

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null)
                return;

            if (ViewModel.SetLaunchScriptCommand.CanExecute(file.Path))
                await ViewModel.SetLaunchScriptCommand.ExecuteAsync(file.Path);
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

        private void DetailsLowerGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _mediaMaxHeight = Math.Max(0, e.NewSize.Height);
            UpdateMediaHeight();
        }

        private void MediaContainer_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateMediaHeight(e.NewSize.Width, null);

        private void UpdateMediaHeight(double? widthOverride = null, double? heightOverride = null)
        {
            if (MediaContainer == null || MediaFlipView == null)
                return;

            var width = widthOverride ?? MediaContainer.ActualWidth;
            var availableHeight = heightOverride ?? (_mediaMaxHeight > 0 ? _mediaMaxHeight : MediaContainer.ActualHeight);

            if (width <= 0 || availableHeight <= 0)
                return;

            double targetHeight = width * 0.5625;
            if (targetHeight > availableHeight)
            {
                targetHeight = availableHeight;
                width = Math.Min(width, availableHeight / 0.5625);
            }

            MediaFlipView.Width = width;
            MediaFlipView.Height = targetHeight;
        }
    }
}
