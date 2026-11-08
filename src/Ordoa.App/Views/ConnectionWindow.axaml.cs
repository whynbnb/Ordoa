using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;
using Ordoa.Core.Localization;

namespace Ordoa.App.Views;

public partial class ConnectionWindow : Window
{
    private CancellationTokenSource? _testCts;

    public ConnectionWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += async (_, _) =>
        {
            if (DataContext is ConnectionDialogViewModel viewModel)
                await viewModel.LoadJumpSecretsAsync();
            if (DataContext is ConnectionDialogViewModel { Host.Length: > 0 })
                NameBox?.Focus();
            else
                HostBox.Focus();
        };
        Closed += (_, _) =>
        {
            try { _testCts?.Cancel(); } catch { /* ignore */ }
        };
    }

    private async void OnDiscoverClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionDialogViewModel viewModel || App.Services is not { } services)
            return;

        var service = await services.Dialogs.ShowNetworkDiscoveryAsync();
        if (service is not null)
            viewModel.ApplyDiscoveredService(service);
    }

    private void OnAddJumpHostClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionDialogViewModel viewModel)
            viewModel.AddJumpHost();
    }

    private void OnRemoveJumpHostClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionDialogViewModel viewModel &&
            sender is Button { DataContext: SshJumpEditorViewModel jump })
        {
            viewModel.RemoveJumpHost(jump);
        }
    }

    private void OnReloadSshProfilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionDialogViewModel vm)
            vm.ReloadSshProfiles();
    }

    private void OnProtocolClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionDialogViewModel vm && sender is Button { Tag: string protocol })
            vm.SetProtocol(protocol);
    }

    private void OnSshAuthClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionDialogViewModel vm && sender is Button { Tag: string method })
            vm.SetSshAuth(method);
    }

    private async void OnBrowsePrivateKeyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionDialogViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L10n.T("选择私钥文件"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All],
        });
        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            vm.PrivateKeyPath = path;
    }

    private void OnTogglePasswordClick(object? sender, RoutedEventArgs e)
    {
        PasswordBox.PasswordChar = PasswordBox.PasswordChar == '\0' ? '●' : '\0';
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionDialogViewModel vm)
            return;

        _testCts?.Cancel();
        _testCts?.Dispose();
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await vm.TestAsync(_testCts.Token);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionDialogViewModel vm)
            return;

        var profile = vm.BuildProfile();
        if (profile is null)
            return;

        Close(new ConnectionDialogResult(profile, vm.Password, vm.SaveProfile, vm.GetJumpPasswords()));
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
