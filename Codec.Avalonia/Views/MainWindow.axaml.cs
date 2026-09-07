using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codec.ViewModels;
using System.Linq;
using Avalonia.Threading;
using Codec.Infrastructure;
using Lucide.Avalonia;
using System.Diagnostics;

namespace Codec.Views;

public sealed partial class MainWindow : Window
{
    private bool _libraryLoaded;
    private AvaloniaWindowService? _windowService;
    private TrayService? _trayService;

    // Only the normal state is worth remembering: a maximized window reports the screen, and a
    // minimized one reports nothing useful.
    private PixelPoint _normalPosition;
    private Size _normalSize;
    private bool _placementCaptureQueued;
    private CancellationTokenSource? _stateShift;

    public MainWindow()
    {
        InitializeComponent();
        _normalSize = ClientSize;
        PositionChanged += OnPositionChanged;
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ClientSizeProperty)
            CapturePlacementSoon();

        if (change.Property == WindowStateProperty)
        {
            // Windows shows a restore glyph once maximized, never a second maximize square.
            if (MaximizeGlyph is not null)
            {
                MaximizeGlyph.Kind = WindowState == WindowState.Maximized
                    ? LucideIconKind.Copy
                    : LucideIconKind.Square;
            }

            if (WindowState != WindowState.Minimized)
                _ = DissolveThroughResizeAsync();
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => CapturePlacementSoon();

    /// <summary>
    /// Carries the content through a window size change.
    ///
    /// The frame itself is animated by the compositor, but the shelf re-columns in a single
    /// frame underneath it, and that snap is what reads as abrupt. Dipping the content across
    /// the change turns it into a dissolve. The class has to leave again or the animation
    /// cannot run a second time.
    /// </summary>
    private async Task DissolveThroughResizeAsync()
    {
        // Details spans the caption row and so is a sibling of the shelf, not a child of it.
        Control[] surfaces = [ContentHost, DetailsHost];
        if (surfaces.Any(surface => surface is null))
            return;

        _stateShift?.Cancel();
        var shift = new CancellationTokenSource();
        _stateShift = shift;

        foreach (Control surface in surfaces)
        {
            surface.Classes.Remove("state-shift");
            surface.Classes.Add("state-shift");
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), shift.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        foreach (Control surface in surfaces)
            surface.Classes.Remove("state-shift");

        if (ReferenceEquals(_stateShift, shift))
            _stateShift = null;

        shift.Dispose();
    }

    /// <summary>
    /// Records the placement once the window state has settled.
    ///
    /// Maximizing changes the client size before it changes WindowState, so reading the size
    /// inline records the maximized rectangle as if it were the user's chosen one. Deferring to
    /// the next dispatcher pass means the state is already correct, and the capture is skipped
    /// entirely unless the window is genuinely in its normal state.
    ///
    /// ClientSize, not FrameSize: Window.Width sets the client area, so saving the outer frame
    /// and restoring it as Width grows the window by the frame inset on every launch.
    /// </summary>
    private void CapturePlacementSoon()
    {
        if (_placementCaptureQueued)
            return;

        _placementCaptureQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _placementCaptureQueued = false;
            if (WindowState != WindowState.Normal)
                return;

            _normalSize = ClientSize;
            _normalPosition = Position;
        }, DispatcherPriority.Background);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        // DWM owns the frame, and it only rounds the corners and darkens the border when told.
        WindowChrome.ApplyNativeFrame(this);

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
        // Saved first, so it is recorded whether this close ends the process or hides to tray.
        if (DataContext is MainViewModel current)
        {
            current.SaveWindowPlacement(
                _normalPosition.X,
                _normalPosition.Y,
                _normalSize.Width,
                _normalSize.Height,
                WindowState == WindowState.Maximized);
        }

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
