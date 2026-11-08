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

    private void OnAddAttributeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PropertiesDialogViewModel vm)
            vm.AddAttribute();
    }

    private void OnRemoveAttributeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PropertiesDialogViewModel vm &&
            sender is Button { DataContext: ExtendedAttributeEditorViewModel attribute })
        {
            vm.RemoveAttribute(attribute);
        }
    }

    private async void OnSaveAttributesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PropertiesDialogViewModel vm)
            await vm.SaveAttributesAsync();
    }

    private async void OnApplyAclClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PropertiesDialogViewModel vm)
            await vm.ApplyAclAsync();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
