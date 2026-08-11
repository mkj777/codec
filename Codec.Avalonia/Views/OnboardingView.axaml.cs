using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Codec.ViewModels;

namespace Codec.Views;

public sealed partial class OnboardingView : UserControl
{
    private int _stage;
    private bool _scanOnStartup;

    public OnboardingView() => InitializeComponent();

    private void Begin_Click(object? sender, RoutedEventArgs e) => ShowStage(1);

    private async void Skip_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await viewModel.MaybeLaterFromOnboardingCommand.ExecuteAsync(null);
    }

    private async void FindGames_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        _scanOnStartup = true;
        await viewModel.ScanGamesCommand.ExecuteAsync(null);
        viewModel.IsOnboardingVisible = true;
        ShowStage(2);
    }

    private void Continue_Click(object? sender, RoutedEventArgs e) => ShowStage(2);

    private async void OpenLibrary_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        await viewModel.CompleteOnboardingAsync(_scanOnStartup, launchSteamSilent: false);
        viewModel.IsOnboardingVisible = false;
    }

    private void ShowStage(int stage)
    {
        _stage = Math.Clamp(stage, 0, 2);
        IntroStep.IsVisible = _stage == 0;
        SetupStep.IsVisible = _stage == 1;
        ReadyStep.IsVisible = _stage == 2;

        IBrush active = this.FindResource("CodecPrimaryBrush") as IBrush ?? Brushes.Orange;
        IBrush inactive = this.FindResource("CodecBorderBrush") as IBrush ?? Brushes.Gray;
        Progress0.Width = _stage == 0 ? 20 : 5;
        Progress1.Width = _stage == 1 ? 20 : 5;
        Progress2.Width = _stage == 2 ? 20 : 5;
        Progress0.Background = _stage == 0 ? active : inactive;
        Progress1.Background = _stage == 1 ? active : inactive;
        Progress2.Background = _stage == 2 ? active : inactive;
    }
}
