using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codec.Media;
using Codec.ViewModels;
using System.ComponentModel;

namespace Codec.Views;

public sealed partial class GameDetailView : UserControl
{
    // The three layouts, measured across the page rather than the window: the rail can be
    // open or closed and the page is what has to hold the reading line.
    private const double ComfortableWidth = 1080d;
    private const double CompactWidth = 820d;

    // The band the continuous metrics scale across. Below the first the page is as tight as
    // it gets; above the second there is nothing left to gain from more type.
    private const double MetricMinWidth = 740d;
    private const double MetricMaxWidth = 1600d;

    private MainViewModel? _viewModel;

    public static readonly StyledProperty<double> DetailsValueFontSizeProperty =
        AvaloniaProperty.Register<GameDetailView, double>(nameof(DetailsValueFontSize), 13d);

    public static readonly StyledProperty<double> DetailsPlatformIconSizeProperty =
        AvaloniaProperty.Register<GameDetailView, double>(nameof(DetailsPlatformIconSize), 22d);

    /// <summary>Metadata values grow with the page, 13 at its narrowest to 15 at its widest.</summary>
    public double DetailsValueFontSize
    {
        get => GetValue(DetailsValueFontSizeProperty);
        set => SetValue(DetailsValueFontSizeProperty, value);
    }

    /// <summary>Platform logos scale with the page in the same band.</summary>
    public double DetailsPlatformIconSize
    {
        get => GetValue(DetailsPlatformIconSizeProperty);
        set => SetValue(DetailsPlatformIconSizeProperty, value);
    }

    public GameDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => StopAllVideos();

        // Tunnelling, because the drawer has to answer a click on the page before whatever
        // sits under the pointer gets to.
        AddHandler(PointerPressedEvent, OnRootPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Closes whatever the details page itself is holding open, one layer at a time. Back,
    /// Escape and right-click all come through here.
    /// </summary>
    public bool TryCloseTransientLayer()
    {
        if (RemoveConfirmation.IsVisible)
        {
            RemoveConfirmation.IsVisible = false;
            return true;
        }

        if (_viewModel?.IsGameSettingsOpen == true)
        {
            _viewModel.CloseGameSettingsCommand.Execute(null);
            return true;
        }

        return false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty)
            ApplyResponsiveLayout(Bounds.Width);
    }

    /// <summary>
    /// Three layouts and two continuously scaled metrics, the way the WinUI page worked.
    ///
    /// The page carries a fixed inset, a meta column and a column of copy; at a narrow width
    /// those three cannot all keep their measure, so the insets close up and eventually the
    /// meta rows drop below the copy rather than squeezing it.
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        // Bounds settles during the first arrange, which can land before the named parts of
        // the page are there to be styled.
        if (width <= 0 || PageRoot is null)
            return;

        bool tight = width < CompactWidth;
        bool compact = !tight && width < ComfortableWidth;

        PageRoot.Classes.Set("tight", tight);
        PageRoot.Classes.Set("compact", compact);
        PageRoot.Classes.Set("comfortable", !tight && !compact);

        // Zero in the tight layout, where the meta rows move below the copy instead.
        var metaWidth = new GridLength(tight ? 0 : compact ? 220 : 260);
        HeroCopyGrid.ColumnDefinitions[1].Width = metaWidth;
        LowerSplitGrid.ColumnDefinitions[1].Width = metaWidth;
        DetailsValueFontSize = Scale(width, 13d, 15d);
        DetailsPlatformIconSize = Scale(width, 16d, 22d);
    }

    private static double Scale(double width, double min, double max)
    {
        double progress = (Math.Clamp(width, MetricMinWidth, MetricMaxWidth) - MetricMinWidth)
            / (MetricMaxWidth - MetricMinWidth);
        return min + ((max - min) * progress);
    }

    /// <summary>
    /// A click anywhere outside the Game Settings drawer closes it, which is how every other
    /// light-dismissible surface in Codec behaves. The gear that opened it is excluded, or the
    /// click would close the drawer here and the command would reopen it a moment later.
    /// </summary>
    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel?.IsGameSettingsOpen != true ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            _viewModel.IsMediaOverlayOpen ||
            _viewModel.IsFranchiseOverlayOpen)
        {
            return;
        }

        if (IsWithin(e.Source as Visual, SettingsDrawer) || IsWithin(e.Source as Visual, GameSettingsButton))
            return;

        RemoveConfirmation.IsVisible = false;
        _viewModel.CloseGameSettingsCommand.Execute(null);
    }

    private static bool IsWithin(Visual? source, Visual ancestor)
    {
        for (Visual? current = source; current != null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
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

    private void CloseMediaOverlay_Click(object? sender, RoutedEventArgs e) => CloseMediaOverlay();

    /// <summary>The scrim is part of the overlay's dismiss surface, not a wall.</summary>
    private void MediaScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseMediaOverlay();
        e.Handled = true;
    }

    private void FranchiseScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _viewModel?.CloseFranchiseOverlayCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// The carousel shows one item at a time and has no navigation of its own, so without
    /// these only the thumbnail that was clicked could ever be seen.
    /// </summary>
    private void PreviousMedia_Click(object? sender, RoutedEventArgs e) => MediaCarousel.Previous();

    private void NextMedia_Click(object? sender, RoutedEventArgs e) => MediaCarousel.Next();

    /// <summary>
    /// One step through the open media overlay, forward for a positive direction. The window
    /// routes the arrow keys here so they land on the same carousel the buttons drive.
    /// </summary>
    public void StepMedia(int direction)
    {
        if (direction > 0)
            MediaCarousel.Next();
        else
            MediaCarousel.Previous();
    }

    private void CloseMediaOverlay()
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
