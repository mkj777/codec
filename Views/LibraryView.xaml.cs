using Codec.Models;
using Codec.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

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
