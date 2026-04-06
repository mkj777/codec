using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace Codec.Views
{
    public sealed partial class GameDetailView : UserControl
    {
        private MainViewModel? ViewModel => DataContext as MainViewModel;
        private double _mediaMaxHeight;

        public GameDetailView()
        {
            InitializeComponent();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
            => ViewModel?.BackCommand.Execute(null);

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
