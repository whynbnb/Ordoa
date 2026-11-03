using Avalonia.Controls;
using Ordoa.App.Services;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Accept();
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        if (DataContext is not PromptDialogViewModel vm || !vm.CanConfirm)
            return;
        Close(vm.Value.Trim());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
