using Avalonia.Controls;
using Avalonia.Interactivity;
using Codec.ViewModels;

namespace Codec.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            ViewModel.IsSettingsVisible = false;
    }

    private void ShowDisconnect_Click(object? sender, RoutedEventArgs e) => DisconnectConfirmation.IsVisible = true;
    private void HideDisconnect_Click(object? sender, RoutedEventArgs e) => DisconnectConfirmation.IsVisible = false;

    private async void ConfirmDisconnect_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.DisconnectSteamCommand.CanExecute(null) == true)
            await ViewModel.DisconnectSteamCommand.ExecuteAsync(null);
        DisconnectConfirmation.IsVisible = false;
    }

    private void ShowReset_Click(object? sender, RoutedEventArgs e) => ResetConfirmation.IsVisible = true;
    private void HideReset_Click(object? sender, RoutedEventArgs e) => ResetConfirmation.IsVisible = false;
}
