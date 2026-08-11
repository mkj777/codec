using Avalonia.Controls;
using Avalonia.Interactivity;
using Codec.ViewModels;

namespace Codec.Views;

public sealed partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void ClearSourceFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SelectedImportFilter = null;
    }
}
