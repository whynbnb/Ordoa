using Avalonia.Controls;
using Ordoa.App.Services;
using Avalonia.Interactivity;

namespace Ordoa.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
