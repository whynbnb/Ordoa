using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class OperationHistoryWindow : Window
{
    public OperationHistoryWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OperationHistoryDialogViewModel vm)
            vm.Clear();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
