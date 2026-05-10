using Codec.Models;
using Codec.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Codec.Views
{
    public sealed partial class LibraryView : UserControl
    {
        private MainViewModel? ViewModel => DataContext as MainViewModel;

        public LibraryView()
        {
            InitializeComponent();
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
                if (ViewModel != null)
                    ViewModel.SelectedSortIndex = idx;
                SortFlyout.Hide();
                UpdateSortOptionHighlight(idx);
            }
        }

        private void UpdateSortOptionHighlight(int activeIndex)
        {
            SetSortButtonActive(SortAlphaButton, activeIndex == 0);
            SetSortButtonActive(SortFolderSizeButton, activeIndex == 1);
            SetSortButtonActive(SortDateAddedButton, activeIndex == 2);
        }

        private static void SetSortButtonActive(Button button, bool isActive)
        {
            button.Foreground = new SolidColorBrush(isActive
                ? Colors.White
                : Windows.UI.Color.FromArgb(255, 136, 136, 136));
        }
    }
}
