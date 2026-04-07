using System;
using Codec.Models;
using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codec.Views
{
    public sealed partial class LibraryView : UserControl
    {
        private const double BaseCoverWidth = 210d;
        private const double CoverAspectRatio = 210d / 315d;
        private const double CoverGap = 12d;

        private MainViewModel? ViewModel => DataContext as MainViewModel;

        public LibraryView()
        {
            InitializeComponent();
            Loaded += LibraryView_Loaded;
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

        private void LibraryView_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateCoverLayout();
        }

        private void LibraryScroller_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCoverLayout(e.NewSize.Width);
        }

        private void UpdateCoverLayout(double availableWidth = double.NaN)
        {
            if (double.IsNaN(availableWidth) || availableWidth <= 0)
            {
                availableWidth = LibraryScroller.ActualWidth;
            }

            if (availableWidth <= 0)
            {
                return;
            }

            double horizontalPadding = LibraryScroller.Padding.Left + LibraryScroller.Padding.Right;
            double viewportWidth = Math.Max(0d, availableWidth - horizontalPadding);
            int columns = Math.Max(1, (int)Math.Floor((viewportWidth + CoverGap) / (BaseCoverWidth + CoverGap)));
            double coverWidth = Math.Max(1d, (viewportWidth - ((columns - 1) * CoverGap)) / columns);
            double coverHeight = Math.Max(1d, coverWidth / CoverAspectRatio);

            if (!AreClose(LibraryItemsLayout.MinItemWidth, coverWidth))
            {
                LibraryItemsLayout.MinItemWidth = coverWidth;
            }

            if (!AreClose(LibraryItemsLayout.MinItemHeight, coverHeight))
            {
                LibraryItemsLayout.MinItemHeight = coverHeight;
            }
        }

        private static bool AreClose(double left, double right)
        {
            return Math.Abs(left - right) < 0.5d;
        }
    }
}
