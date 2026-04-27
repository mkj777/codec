using Codec.Models;
using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;

namespace Codec.Views
{
    public sealed partial class FranchiseTimelineControl : UserControl
    {
        public static readonly DependencyProperty ItemsProperty =
            DependencyProperty.Register(
                nameof(Items),
                typeof(IList<FranchiseTimelineItem>),
                typeof(FranchiseTimelineControl),
                new PropertyMetadata(null));

        public IList<FranchiseTimelineItem>? Items
        {
            get => (IList<FranchiseTimelineItem>?)GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public FranchiseTimelineControl() => InitializeComponent();
    }
}
