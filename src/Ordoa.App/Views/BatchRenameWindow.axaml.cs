using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class BatchRenameWindow : Window
{
    public BatchRenameWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += (_, _) =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        };
        Closed += (_, _) => (DataContext as BatchRenameDialogViewModel)?.Dispose();
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BatchRenameDialogViewModel vm)
            return;

        var plan = vm.BuildResult();
        if (plan is null)
            return;

        Close(plan);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            Close(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
