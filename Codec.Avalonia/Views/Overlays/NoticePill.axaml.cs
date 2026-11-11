using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Codec.ViewModels;

namespace Codec.Views.Overlays;

public sealed partial class NoticePill : UserControl
{
    private MainViewModel? _viewModel;

    public NoticePill()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch(DataContext as MainViewModel);
        NoticeLine.PropertyChanged += NoticeLine_PropertyChanged;
    }

    private void Watch(MainViewModel? viewModel)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

        _viewModel = viewModel;
        if (_viewModel != null)
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        Release();
    }

    /// <summary>
    /// Work in flight rewrites its line with every percent, and the pill is centred, so each
    /// new width would re-centre it and walk the dot back and forth. While work runs the line
    /// only ever widens; the next notice starts from its own width again.
    /// </summary>
    private void NoticeLine_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != BoundsProperty || _viewModel?.IsNoticeBusy != true)
            return;

        double width = NoticeLine.Bounds.Width;
        if (width > NoticeLine.MinWidth)
            NoticeLine.MinWidth = width;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsNoticeBusy) && _viewModel?.IsNoticeBusy != true)
            Release();
    }

    private void Release() => NoticeLine.MinWidth = 0;
}
