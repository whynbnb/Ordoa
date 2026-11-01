using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnLanguageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm && sender is Button { Tag: string language })
            vm.SetLanguage(language);
    }

    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm && sender is Button { Tag: string theme })
            vm.SetTheme(theme);
    }

    private void OnViewClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm && sender is Button { Tag: string view })
            vm.SetView(view);
    }

    private void OnAccentClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm && sender is Border { Tag: string accent })
            vm.SetAccent(accent);
    }

    private void OnResetPlacesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
            vm.ResetPlaces();
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
            vm.Reset();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
            Close(vm.ToSettings());
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
