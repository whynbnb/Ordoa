using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class DiskUsageWindow : Window
{
    public DiskUsageWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += async (_, _) =>
        {
            if (DataContext is DiskUsageDialogViewModel vm)
                await vm.StartAsync();
        };
        Closed += (_, _) => (DataContext as DiskUsageDialogViewModel)?.Dispose();
    }

    private async void OnUpClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiskUsageDialogViewModel vm)
            await vm.GoUpAsync();
    }

    private async void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiskUsageDialogViewModel vm)
            await vm.RescanAsync();
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
        => (DataContext as DiskUsageDialogViewModel)?.Cancel();

    private async void OnEntryActivated(object? sender, TreemapEntryEventArgs e)
    {
        if (DataContext is DiskUsageDialogViewModel vm)
            await vm.OpenEntryAsync(e.Entry);
    }

    private async void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DiskUsageDialogViewModel vm)
            await vm.OpenSelectedAsync();
    }

    private async void OnOpenSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiskUsageDialogViewModel vm)
            await vm.OpenSelectedAsync();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiskUsageDialogViewModel vm)
            vm.Cancel();
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is DiskUsageDialogViewModel vm)
                vm.Cancel();
            Close();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
