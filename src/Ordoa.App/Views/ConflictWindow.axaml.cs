using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.ViewModels.Dialogs;
using Ordoa.Core.Services;

namespace Ordoa.App.Views;

public partial class ConflictWindow : Window
{
    public ConflictWindow()
    {
        InitializeComponent();
    }

    private void OnReplaceClick(object? sender, RoutedEventArgs e)
        => Close(Result(ConflictChoice.Replace, ConflictChoice.ReplaceAll));

    private void OnSkipClick(object? sender, RoutedEventArgs e)
        => Close(Result(ConflictChoice.Skip, ConflictChoice.SkipAll));

    private void OnRenameClick(object? sender, RoutedEventArgs e)
        => Close(ConflictChoice.Rename);

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close(ConflictChoice.Cancel);

    private ConflictChoice Result(ConflictChoice single, ConflictChoice all)
        => DataContext is ConflictDialogViewModel { ApplyAll: true } ? all : single;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(ConflictChoice.Cancel);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
