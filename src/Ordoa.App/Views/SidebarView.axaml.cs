using Avalonia.Controls;
using Avalonia.Input;
using Ordoa.App.ViewModels;
using Ordoa.Core.Localization;

namespace Ordoa.App.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private async void OnEntryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SidebarEntryViewModel entry } && ViewModel is { } vm)
            await vm.ActivateSidebarEntryAsync(entry);
    }

    private void OnEntryContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { DataContext: SidebarEntryViewModel entry } button || ViewModel is not { } vm)
            return;
        if (entry.Kind is SidebarEntryKind.AddBookmark or SidebarEntryKind.AddConnection or SidebarEntryKind.Action)
            return;

        var menu = new ContextMenu();
        menu.Items.Add(CreateItem(L10n.T("在新标签页中打开"), () => vm.OpenSidebarEntryInNewTabAsync(entry)));
        menu.Items.Add(CreateItem(L10n.T("在分屏中打开"), () => vm.OpenSidebarEntryInSplitAsync(entry)));

        if (entry.Kind is SidebarEntryKind.Volume or SidebarEntryKind.Place or SidebarEntryKind.Bookmark or SidebarEntryKind.Recent)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateItem(L10n.T("在终端中打开"), () => vm.OpenTerminalForSidebarEntryAsync(entry)));
        }

        if (entry.Kind == SidebarEntryKind.Bookmark)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateItem(L10n.T("移除书签"), () => vm.RemoveBookmarkAsync(entry)));
        }

        if (entry.Kind == SidebarEntryKind.Connection)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateItem(L10n.T("编辑连接…"), () => vm.EditConnectionAsync(entry)));
            menu.Items.Add(CreateItem(L10n.T("移除连接"), () => vm.RemoveConnectionAsync(entry)));
        }

        button.ContextMenu = menu;
        menu.Open(button);
        e.Handled = true;
    }

    private static MenuItem CreateItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch
            {
                // Errors are surfaced by the view model.
            }
        };
        return item;
    }

    private async void OnSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.ShowSettingsAsync();
    }

    private async void OnAboutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.Services.Dialogs.ShowAboutAsync();
    }
}
