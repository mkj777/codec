using Codec.Models;
using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    }
}
