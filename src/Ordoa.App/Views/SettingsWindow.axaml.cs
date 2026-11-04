using Avalonia.Controls;
using Ordoa.App.Services;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.ViewModels.Dialogs;
using Ordoa.Core.Localization;

namespace Ordoa.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += async (_, _) =>
        {
            if (DataContext is SettingsDialogViewModel { IsLinux: true } vm &&
                App.Services is { } services)
            {
                vm.SetIntegrationStatus(await services.Integration.GetStatusAsync());
            }
        };
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

    private void OnWindowButtonModeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm && sender is Button { Tag: string mode })
            vm.SetWindowButtonMode(mode);
    }

    private void OnResetPlacesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
            vm.ResetPlaces();
    }

    private void OnAddCustomPathClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
            vm.AddCustomPath();
    }

    private void OnRemoveCustomPathClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm && sender is Button { Tag: string path })
            vm.RemoveCustomPath(path);
    }

    private async void OnRegisterFileManagerClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel vm || App.Services is not { } services)
            return;

        vm.IsRegisteringFileManager = true;
        vm.SetIntegrationStatus(L10n.T("正在注册…"));
        try
        {
            var result = await services.Integration.RegisterAsDefaultFileManagerAsync();
            vm.SetIntegrationStatus(result.Message);
            if (result.Success && !await services.StartFileManagerServiceAsync(force: true))
            {
                vm.SetIntegrationStatus(L10n.T("已写入注册文件，但 D-Bus 名称被其他文件管理器占用。"));
            }
        }
        catch (Exception ex)
        {
            vm.SetIntegrationStatus(ex.Message);
        }
        finally
        {
            vm.IsRegisteringFileManager = false;
        }
    }

    private void OnClearOpenWithClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
            vm.ClearOpenWithDefaults();
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
