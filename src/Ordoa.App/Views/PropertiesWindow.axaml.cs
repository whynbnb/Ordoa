using Avalonia.Controls;
using Avalonia.Interactivity;
using Ordoa.App.ViewModels.Dialogs;

namespace Ordoa.App.Views;

public partial class PropertiesWindow : Window
{
    public PropertiesWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as PropertiesDialogViewModel)?.Dispose();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
