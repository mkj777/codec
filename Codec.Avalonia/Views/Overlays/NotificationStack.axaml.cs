using Avalonia.Controls;
using Avalonia.Interactivity;
using Codec.ViewModels;

namespace Codec.Views.Overlays;

public sealed partial class NotificationStack : UserControl
{
    public NotificationStack() => InitializeComponent();

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void DismissAdmin_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            ViewModel.IsAdminWarningVisible = false;
    }

    private void DismissUpdateError_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            ViewModel.IsUpdateErrorVisible = false;
    }

    private void DismissNoUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            ViewModel.IsUpdateNoUpdateVisible = false;
    }
}
