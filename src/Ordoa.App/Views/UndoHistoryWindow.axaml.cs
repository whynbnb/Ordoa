using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class UndoHistoryWindow : Window
{
    public UndoHistoryWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Closed += (_, _) => (DataContext as UndoHistoryDialogViewModel)?.Dispose();
    }

    private async void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UndoHistoryDialogViewModel vm)
            await vm.UndoCommand.ExecuteAsync(null);
    }

    private async void OnRedoClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UndoHistoryDialogViewModel vm)
            await vm.RedoCommand.ExecuteAsync(null);
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
