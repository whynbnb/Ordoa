using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ordoa.App.Services;

namespace Ordoa.App.Views;

public partial class ImageConversionWindow : Window
{
    public ImageConversionWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
