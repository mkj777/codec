using Codec.Models;
using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace Codec.Views
{
    public sealed partial class LibraryView : UserControl
    {
        private MainViewModel? _viewModel;

        private MainViewModel? ViewModel => DataContext as MainViewModel;

        public LibraryView()
        {
            InitializeComponent();
            DataContextChanged += LibraryView_DataContextChanged;
        }

        private void LibraryView_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (_viewModel != null)
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

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
                UpdateSortOptionHighlight(_viewModel.SelectedSortIndex);
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
                    tb.IsChecked = true;

                if (ViewModel != null)
                    ViewModel.SelectedSortIndex = idx;

                SortFlyout.Hide();
                UpdateSortOptionHighlight(idx);
            }
        }

        private void LibraryCover_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not Image image)
                return;

            bool isWideArtwork = image.Source is BitmapSource bitmap && bitmap.PixelWidth > bitmap.PixelHeight;
            ApplyLibraryCoverLayout(image, isWideArtwork);
        }

        private void LibraryCover_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is Image image)
                ApplyLibraryCoverLayout(image, false);
        }

        private static void ApplyLibraryCoverLayout(Image image, bool isWideArtwork)
        {
            image.Stretch = isWideArtwork ? Stretch.Uniform : Stretch.UniformToFill;

            if (image.RenderTransform is TranslateTransform translate)
                translate.Y = isWideArtwork ? -image.ActualHeight * 0.1 : 0;

            if (image.Parent is DependencyObject artwork &&
                FindNamedDescendant<Image>(artwork, "WideArtworkBackdrop") is Image backdrop)
                backdrop.Visibility = isWideArtwork ? Visibility.Visible : Visibility.Collapsed;

            if (image.Parent is DependencyObject parent &&
                FindNamedDescendant<FrameworkElement>(parent, "WideArtworkBlur") is UIElement blur)
                blur.Visibility = isWideArtwork ? Visibility.Visible : Visibility.Collapsed;
        }

        private static T? FindNamedDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                if (child is T match && match.Name == name)
                    return match;

                T? descendant = FindNamedDescendant<T>(child, name);
                if (descendant != null)
                    return descendant;
            }

            return null;
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
            SortAlphaAscButton.IsChecked = activeIndex == 0;
            SortAlphaDescButton.IsChecked = activeIndex == 1;
            SortFolderDescButton.IsChecked = activeIndex == 2;
            SortFolderAscButton.IsChecked = activeIndex == 3;
            SortDateDescButton.IsChecked = activeIndex == 4;
            SortDateAscButton.IsChecked = activeIndex == 5;
        }
    }
}
