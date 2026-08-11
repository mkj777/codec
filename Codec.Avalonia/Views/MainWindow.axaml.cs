using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codec.ViewModels;

namespace Codec.Views;

public sealed partial class MainWindow : Window
{
    private bool _libraryLoaded;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_libraryLoaded || DataContext is not MainViewModel viewModel)
            return;

        _libraryLoaded = true;
        await viewModel.LoadLibraryAsync();
        _ = viewModel.StartBackgroundMaintenanceAsync();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key is Key.F or Key.S)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Back or Key.Escape) ||
            e.Source is TextBox ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.IsResetConfirmVisible)
            viewModel.IsResetConfirmVisible = false;
        else if (viewModel.IsSettingsVisible)
            viewModel.IsSettingsVisible = false;
        else if (viewModel.IsMediaOverlayOpen)
            viewModel.CloseMediaOverlayCommand.Execute(null);
        else if (viewModel.IsFranchiseOverlayOpen)
            viewModel.CloseFranchiseOverlayCommand.Execute(null);
        else if (viewModel.IsGameSettingsOpen)
            viewModel.CloseGameSettingsCommand.Execute(null);
        else if (viewModel.IsDetailsVisible && viewModel.BackCommand.CanExecute(null))
            viewModel.BackCommand.Execute(null);
        else
            return;

        e.Handled = true;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
    }

    private void Sidebar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.ToggleSidebarCommand.Execute(null);
        e.Handled = true;
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.IsSettingsVisible = true;
    }

    private void SettingsScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.IsSettingsVisible = false;
        e.Handled = true;
    }
}
