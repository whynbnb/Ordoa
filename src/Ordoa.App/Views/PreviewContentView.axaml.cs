using Avalonia.Controls;
using Avalonia.Interactivity;
using Ordoa.App.ViewModels;

namespace Ordoa.App.Views;

public partial class PreviewContentView : UserControl
{
    public PreviewContentView()
    {
        InitializeComponent();
    }

    private async void OnPreviousPageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FilePreviewViewModel viewModel)
            await viewModel.PreviousPdfPageAsync();
    }

    private async void OnNextPageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FilePreviewViewModel viewModel)
            await viewModel.NextPdfPageAsync();
    }

    private async void OnZoomOutClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FilePreviewViewModel viewModel)
            await viewModel.SetPdfZoomAsync(viewModel.PdfZoom - 25);
    }

    private async void OnZoomInClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FilePreviewViewModel viewModel)
            await viewModel.SetPdfZoomAsync(viewModel.PdfZoom + 25);
    }
}
