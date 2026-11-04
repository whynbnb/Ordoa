using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class OpenWithWindow : Window
{
    public OpenWithWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            if (DataContext is OpenWithDialogViewModel { SelectedApp: null } vm && vm.Apps.Count > 0)
                vm.SelectedApp = vm.Apps[0];
        };
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e) => Accept();
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSystemDefaultClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OpenWithDialogViewModel vm)
            Close(vm.BuildSystemDefaultResult());
    }

    private void OnAppDoubleTapped(object? sender, TappedEventArgs e) => Accept();

    private void Accept()
    {
        if (DataContext is not OpenWithDialogViewModel vm)
            return;

        var result = vm.BuildResult();
        if (result is not null)
            Close(result);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            Close(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !e.Handled)
        {
            Accept();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
