using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class DuplicateFinderWindow : Window
{
    public DuplicateFinderWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += async (_, _) =>
        {
            if (DataContext is DuplicateFinderDialogViewModel vm)
                await vm.StartAsync();
        };
        Closed += (_, _) => (DataContext as DuplicateFinderDialogViewModel)?.Dispose();
    }

    private async void OnScanClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DuplicateFinderDialogViewModel vm)
            await vm.RescanAsync();
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
        => (DataContext as DuplicateFinderDialogViewModel)?.Cancel();

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
        => (DataContext as DuplicateFinderDialogViewModel)?.SelectAllDuplicates();

    private void OnClearAllClick(object? sender, RoutedEventArgs e)
        => (DataContext as DuplicateFinderDialogViewModel)?.ClearSelection();

    private void OnSelectGroupDuplicatesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DuplicateGroupViewModel group })
            group.SelectDuplicates();
    }

    private void OnClearGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DuplicateGroupViewModel group })
            group.ClearSelection();
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DuplicateFinderDialogViewModel vm)
            await vm.DeleteSelectedAsync();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DuplicateFinderDialogViewModel vm)
            vm.Cancel();
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is DuplicateFinderDialogViewModel vm)
                vm.Cancel();
            Close();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
