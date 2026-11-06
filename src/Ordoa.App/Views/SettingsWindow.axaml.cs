using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Ordoa.App.Services;
using Ordoa.App.ViewModels;
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
            if (DataContext is not SettingsDialogViewModel vm || App.Services is not { } services)
                return;

            vm.SetUndoStatus(L10n.Format("撤销历史 {0} 条 · 可重做 {1} 条",
                services.Undo.Count, services.Undo.RedoCount));

            if (vm.IsLinux)
                vm.SetIntegrationStatus(await services.Integration.GetStatusAsync());
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

    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (SettingsScroll is not null)
                SettingsScroll.Offset = new Vector(0, 0);
        });
    }

    private void OnDuplicateConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || DataContext is not SettingsDialogViewModel vm ||
            App.Services is not { } services)
        {
            return;
        }

        services.FileSystems.DuplicateProfile(id);
        vm.ReloadConnections();
    }

    private async void OnDeleteConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || DataContext is not SettingsDialogViewModel vm ||
            App.Services is not { } services)
        {
            return;
        }

        var profile = services.FileSystems.Connections.Get(id);
        if (profile is null)
            return;

        var confirmed = await services.Dialogs.ConfirmAsync(
            L10n.T("删除远程连接？"),
            L10n.Format("将删除连接“{0}”及其保存的密码。", profile.Name),
            L10n.T("删除"), destructive: true);
        if (!confirmed)
            return;

        await services.FileSystems.RemoveProfileAsync(id);
        foreach (var window in services.Windows.Windows)
        {
            if (window.DataContext is MainViewModel main)
                await main.HandleConnectionRemovedAsync(id);
        }
        vm.ReloadConnections();
    }

    private void OnClearUndoHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel vm || App.Services is not { } services)
            return;

        services.Undo.Clear();
        vm.SetUndoStatus(L10n.T("撤销历史已清除"));
    }

    private void OnTerminalModeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm && sender is Button { Tag: string mode })
            vm.SetTerminalMode(mode);
    }

    private void OnTerminalDefaultClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm && sender is RadioButton { Tag: string id })
            vm.SetDefaultTerminal(id);
    }

    private void OnRescanTerminalsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
            vm.ReloadTerminals();
    }

    private async void OnBrowseTerminalClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L10n.T("选择终端可执行文件"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All],
        });
        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        vm.ManualTerminalPath = path;
        if (string.IsNullOrWhiteSpace(vm.ManualTerminalName))
            vm.ManualTerminalName = Path.GetFileNameWithoutExtension(path);
    }

    private void OnAddCustomCommandClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
        {
            vm.AddCustomCommand();
            Dispatcher.UIThread.Post(() =>
            {
                if (SettingsScroll is not null)
                    SettingsScroll.Offset = new Vector(0, SettingsScroll.Extent.Height);
            });
        }
    }

    private void OnRemoveCustomCommandClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm && sender is Button { Tag: string id })
            vm.RemoveCustomCommand(id);
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
        {
            if (!vm.ValidateTerminalSettings())
            {
                vm.SelectedCategoryIndex = 4;
                return;
            }
            Close(vm.ToSettings());
        }
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
