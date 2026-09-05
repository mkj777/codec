using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Codec.ViewModels;
using System.Diagnostics;

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

        // The scan step shows itself first, then reports itself done. The flag keeps
        // ScanGamesAsync from hiding onboarding out from under the step being shown.
        viewModel.IsScanScreenComplete = false;
        viewModel.IsOnboardingScanActive = true;
        ShowStage(2);

        try
        {
            await viewModel.ScanGamesCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // A failed scan must still let the player reach their shelf.
            Debug.WriteLine($"[Onboarding] scan failed: {ex}");
        }
        finally
        {
            viewModel.IsOnboardingScanActive = false;
            viewModel.IsOnboardingVisible = true;
            viewModel.IsScanScreenComplete = true;
        }
    }

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.IsScanScreenComplete = true;

        ShowStage(2);
    }

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

        // The active dot stretches into a short bar; the rest stay quiet.
        IBrush active = this.FindResource("CodecPrimaryBrush") as IBrush ?? Brushes.Orange;
        IBrush inactive = this.FindResource("CodecTextMutedBrush") as IBrush ?? Brushes.Gray;
        Border[] dots = [Progress0, Progress1, Progress2];

        for (int index = 0; index < dots.Length; index++)
        {
            bool isActive = index == _stage;
            dots[index].Width = isActive ? 18 : 5;
            dots[index].Background = isActive ? active : inactive;
            dots[index].Opacity = isActive ? 1 : 0.35;
        }
    }
}
