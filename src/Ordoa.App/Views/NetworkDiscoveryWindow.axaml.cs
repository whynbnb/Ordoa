using Avalonia.Controls;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class NetworkDiscoveryWindow : Window
{
    public NetworkDiscoveryWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += async (_, _) =>
        {
            if (DataContext is NetworkDiscoveryDialogViewModel viewModel)
                await viewModel.ScanAsync();
        };
        Closed += (_, _) => (DataContext as NetworkDiscoveryDialogViewModel)?.Dispose();
    }

    private async void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NetworkDiscoveryDialogViewModel viewModel)
            await viewModel.ScanAsync();
    }

    private void OnUseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NetworkDiscoveryDialogViewModel { SelectedService: { CanUse: true } service })
            Close(service);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NetworkDiscoveryDialogViewModel viewModel)
            viewModel.Cancel();
        Close(null);
    }
}
