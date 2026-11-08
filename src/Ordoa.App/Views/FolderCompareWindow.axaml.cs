using Avalonia.Controls;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class FolderCompareWindow : Window
{
    public FolderCompareWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += async (_, _) =>
        {
            if (DataContext is FolderCompareDialogViewModel vm)
                await vm.ScanAsync();
        };
        Closed += (_, _) => (DataContext as FolderCompareDialogViewModel)?.Dispose();
    }

    private async void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FolderCompareDialogViewModel vm)
            await vm.ScanAsync();
    }

    private async void OnSyncRightClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FolderCompareDialogViewModel vm)
            await vm.SyncAsync(toRight: true);
    }

    private async void OnSyncLeftClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FolderCompareDialogViewModel vm)
            await vm.SyncAsync(toRight: false);
    }

    private async void OnRowCopyRightClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FolderCompareDialogViewModel vm &&
            sender is Button { DataContext: FolderCompareRowViewModel row })
        {
            await vm.SyncRowAsync(row, toRight: true);
        }
    }

    private async void OnRowCopyLeftClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FolderCompareDialogViewModel vm &&
            sender is Button { DataContext: FolderCompareRowViewModel row })
        {
            await vm.SyncRowAsync(row, toRight: false);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FolderCompareDialogViewModel vm)
            vm.Cancel();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
