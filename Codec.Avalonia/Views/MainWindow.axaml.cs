using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codec.ViewModels;
using Codec.Infrastructure;
using System.Diagnostics;

namespace Codec.Views;

public sealed partial class MainWindow : Window
{
    private bool _libraryLoaded;
    private AvaloniaWindowService? _windowService;
    private TrayService? _trayService;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    public void ConfigureLifecycle(AvaloniaWindowService windowService, TrayService trayService)
    {
        _windowService = windowService;
        _trayService = trayService;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_libraryLoaded || DataContext is not MainViewModel viewModel)
            return;

        _libraryLoaded = true;

        // The launch sequence covers the load rather than following it, so the two run
        // together and background work only begins once the shelf is really on screen.
        Task load = viewModel.LoadLibraryAsync();
        await Startup.PlayAsync(load);

        if (load.IsFaulted)
            Debug.WriteLine($"[Startup] library load failed: {load.Exception}");

        _ = viewModel.StartBackgroundMaintenanceAsync();
        _ = viewModel.CheckForUpdatesCommand.ExecuteAsync(null);
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

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_windowService?.IsShutdownRequested == true || DataContext is not MainViewModel viewModel)
            return;

        if (viewModel.HasCloseBehaviorChoice && !viewModel.CloseToTray)
            return;

        e.Cancel = true;
        if (viewModel.HasCloseBehaviorChoice)
        {
            _trayService?.Show();
            Hide();
            return;
        }

        viewModel.IsCloseChoiceVisible = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _trayService?.Dispose();
        _trayService = null;
    }

    private async void KeepCodecOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        viewModel.IsCloseChoiceVisible = false;
        await viewModel.SetCloseToTrayAsync(true);
        _trayService?.Show();
        Hide();
    }

    private async void CloseCodec_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        viewModel.IsCloseChoiceVisible = false;
        await viewModel.SetCloseToTrayAsync(false);
        _windowService?.CloseApplication();
    }

    private void CloseChoiceScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.IsCloseChoiceVisible = false;
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

    private void MinimizeWindow_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseWindow_Click(object? sender, RoutedEventArgs e)
        => Close();

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
