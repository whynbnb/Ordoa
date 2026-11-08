using Avalonia.Controls;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class BatchPermissionsWindow : Window
{
    public BatchPermissionsWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Closed += (_, _) => (DataContext as BatchPermissionsDialogViewModel)?.Dispose();
    }

    private async void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BatchPermissionsDialogViewModel vm)
            return;

        if (await vm.ApplyAsync())
            Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
