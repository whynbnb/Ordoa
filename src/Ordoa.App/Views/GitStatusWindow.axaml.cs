using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class GitStatusWindow : Window
{
    public GitStatusWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += async (_, _) =>
        {
            if (DataContext is GitStatusDialogViewModel vm)
                await vm.StartAsync();
        };
        Closed += (_, _) => (DataContext as GitStatusDialogViewModel)?.Dispose();
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GitStatusDialogViewModel vm)
            await vm.RefreshAsync();
    }

    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GitStatusDialogViewModel vm)
            return;
        vm.RevealSelected();
        if (vm.SelectedPath is not null)
            Close(vm.SelectedPath);
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
        => OnRevealClick(sender, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => Close(null);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
