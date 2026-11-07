using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class TransferPreviewWindow : Window
{
    public TransferPreviewWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
    }

    private void OnContinueClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TransferPreviewDialogViewModel vm)
            Close(vm.Choice);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

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
