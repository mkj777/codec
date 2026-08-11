using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codec.Media;
using Codec.ViewModels;
using System.ComponentModel;

namespace Codec.Views;

public sealed partial class GameDetailView : UserControl
{
    private MainViewModel? _viewModel;

    public GameDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => StopAllVideos();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

        _viewModel = DataContext as MainViewModel;
        if (_viewModel != null)
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsMediaOverlayOpen) && _viewModel?.IsMediaOverlayOpen != true)
            StopAllVideos();
    }

    private void CloseMediaOverlay_Click(object? sender, RoutedEventArgs e)
    {
        StopAllVideos();
        _viewModel?.CloseMediaOverlayCommand.Execute(null);
    }

    private void StopAllVideos()
    {
        foreach (CodecVideoView video in this.GetVisualDescendants().OfType<CodecVideoView>())
            video.Stop();
    }

    private void ShowRemoveConfirmation_Click(object? sender, RoutedEventArgs e)
        => RemoveConfirmation.IsVisible = true;

    private void HideRemoveConfirmation_Click(object? sender, RoutedEventArgs e)
        => RemoveConfirmation.IsVisible = false;

    private async void ConfirmRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.DeleteSelectedGameCommand.CanExecute(null) == true)
            await _viewModel.DeleteSelectedGameCommand.ExecuteAsync(null);

        RemoveConfirmation.IsVisible = false;
    }
}
