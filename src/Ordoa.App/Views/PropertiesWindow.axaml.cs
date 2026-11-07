using Avalonia.Controls;
using Ordoa.App.Services;
using Avalonia.Interactivity;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class PropertiesWindow : Window
{
    public PropertiesWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        Opened += async (_, _) =>
        {
            if (DataContext is PropertiesDialogViewModel vm)
                await vm.LoadMetadataAsync();
        };
        Closed += (_, _) => (DataContext as PropertiesDialogViewModel)?.Dispose();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
