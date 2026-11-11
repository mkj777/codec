using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Codec.Controls;
using Codec.ViewModels;

namespace Codec.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WatchViewModel();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private MainViewModel? _watched;

    private void WatchViewModel()
    {
        if (_watched != null)
            _watched.PropertyChanged -= ViewModel_PropertyChanged;
        _watched = ViewModel;
        if (_watched != null)
            _watched.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The row sits at the bottom of the overlay, so without this the list opens below
        // the fold and the chevron turns with nothing visibly happening.
        if (e.PropertyName == nameof(MainViewModel.IsHiddenGamesExpanded) && ViewModel?.IsHiddenGamesExpanded == true)
            Dispatcher.UIThread.Post(() => HiddenGamesList.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            ViewModel.IsSettingsVisible = false;
    }

    private void ShowDisconnect_Click(object? sender, RoutedEventArgs e) => Reveal.SetIsOpen(DisconnectConfirmation, true);
    private void HideDisconnect_Click(object? sender, RoutedEventArgs e) => Reveal.SetIsOpen(DisconnectConfirmation, false);

    private async void ConfirmDisconnect_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.DisconnectSteamCommand.CanExecute(null) == true)
            await ViewModel.DisconnectSteamCommand.ExecuteAsync(null);
        Reveal.SetIsOpen(DisconnectConfirmation, false);
    }

    private void ShowReset_Click(object? sender, RoutedEventArgs e) => Reveal.SetIsOpen(ResetConfirmation, true);
    private void HideReset_Click(object? sender, RoutedEventArgs e) => Reveal.SetIsOpen(ResetConfirmation, false);

}
