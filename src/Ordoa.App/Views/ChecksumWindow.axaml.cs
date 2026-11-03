using Avalonia.Controls;
using Ordoa.App.Services;
using Avalonia.Interactivity;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class ChecksumWindow : Window
{
    public ChecksumWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += async (_, _) =>
        {
            if (DataContext is ChecksumDialogViewModel vm)
                await vm.StartAsync();
        };
        Closed += (_, _) => (DataContext as ChecksumDialogViewModel)?.Dispose();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChecksumDialogViewModel { IsRunning: true } vm)
            vm.CancelCommand.Execute(null);
        Close();
    }
}
