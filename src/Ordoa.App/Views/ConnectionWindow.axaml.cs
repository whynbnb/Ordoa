using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.Services;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class ConnectionWindow : Window
{
    private CancellationTokenSource? _testCts;

    public ConnectionWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += (_, _) =>
        {
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

    private void OnProtocolClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionDialogViewModel vm && sender is Button { Tag: string protocol })
            vm.SetProtocol(protocol);
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

        Close(new ConnectionDialogResult(profile, vm.Password, vm.SaveProfile));
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
