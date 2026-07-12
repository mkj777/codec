using Codec.Models;
using Codec.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using Windows.UI.ViewManagement;

namespace Codec.Views
{
    public sealed partial class LibraryView : UserControl
    {
        private MainViewModel? _viewModel;
        private readonly UISettings _uiSettings = new();

        private MainViewModel? ViewModel => DataContext as MainViewModel;

        public LibraryView()
        {
            InitializeComponent();
            DataContextChanged += LibraryView_DataContextChanged;
        }

        private void LibraryView_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            _viewModel = args.NewValue as MainViewModel;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
                UpdateSortOptionHighlight(_viewModel.SelectedSortIndex);
            }
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedSortIndex) && _viewModel != null)
            {
                UpdateSortOptionHighlight(_viewModel.SelectedSortIndex);
            }
        }

        private void LibraryItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var game = fe.Tag as Game ?? fe.DataContext as Game;
                if (game != null)
                    ViewModel?.SelectGameCommand.Execute(game);
            }
        }

        private void SortOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int idx))
            {
                if (fe is ToggleButton tb)
                {
                    tb.IsChecked = true;
                }
                if (ViewModel != null)
                    ViewModel.SelectedSortIndex = idx;
                SortFlyout.Hide();
                UpdateSortOptionHighlight(idx);
            }
        }

        private void LibraryCover_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (!_uiSettings.AnimationsEnabled || sender is not Image image)
                return;

            double targetOpacity = (image.DataContext as Game)?.LibraryCardOpacity ?? 1d;
            var fade = new DoubleAnimation
            {
                From = 0,
                To = targetOpacity,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            Storyboard.SetTarget(fade, image);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }

        private void InstallFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton selected || !int.TryParse(selected.Tag?.ToString(), out int value))
                return;

            InstallFilterAll.IsChecked = value == 0;
            InstallFilterInstalled.IsChecked = value == 1;
            InstallFilterOwnedOnly.IsChecked = value == 2;
            if (ViewModel != null)
                ViewModel.SelectedInstallFilter = value;
        }

        private void UpdateSortOptionHighlight(int activeIndex)
        {
            SortAlphaAscButton.IsChecked = (activeIndex == 0);
            SortAlphaDescButton.IsChecked = (activeIndex == 1);
            SortFolderDescButton.IsChecked = (activeIndex == 2);
            SortFolderAscButton.IsChecked = (activeIndex == 3);
            SortDateDescButton.IsChecked = (activeIndex == 4);
            SortDateAscButton.IsChecked = (activeIndex == 5);
        }
    }
}
