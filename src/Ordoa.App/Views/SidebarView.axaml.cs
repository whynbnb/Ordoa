using Avalonia.Controls;
using Avalonia.Input;
using Ordoa.App.Services;
using Ordoa.App.ViewModels;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;

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

    private void OnEntryDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Button { DataContext: SidebarEntryViewModel entry } || ViewModel is not { } vm)
            return;

        var payload = FileDropHelper.GetPayload(e.DataTransfer);
        var sources = payload?.Sources;
        if (!FileDropHelper.HasData(e.DataTransfer, payload) ||
            !CanDrop(entry, sources, payload is null, vm))
        {
            entry.IsDropTarget = false;
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var move = entry.Kind == SidebarEntryKind.Trash ||
                   FileDropHelper.IsMove(e.KeyModifiers, sources, TargetFileSystemId(entry), payload is null);
        e.DragEffects = move ? DragDropEffects.Move : DragDropEffects.Copy;
        entry.IsDropTarget = true;
        e.Handled = true;
    }

    private static bool CanDrop(SidebarEntryViewModel entry, IReadOnlyList<OperationSource>? sources,
        bool external, MainViewModel vm)
    {
        if (string.IsNullOrEmpty(entry.Path))
            return false;

        switch (entry.Kind)
        {
            case SidebarEntryKind.Place:
            case SidebarEntryKind.Volume:
            case SidebarEntryKind.Recent:
            case SidebarEntryKind.Bookmark:
            case SidebarEntryKind.Connection:
                return true;
            case SidebarEntryKind.Trash:
                return vm.Services.Trash.IsSupported &&
                       (external || FileDropHelper.IsTrashable(sources));
            default:
                return false;
        }
    }

    private static string TargetFileSystemId(SidebarEntryViewModel entry)
        => entry.Kind == SidebarEntryKind.Trash
            ? "trash"
            : entry.FileSystemId is { Length: > 0 } id ? id : "local";

    private void OnEntryDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Button { DataContext: SidebarEntryViewModel entry })
            entry.IsDropTarget = false;
    }

    private async void OnEntryDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Button { DataContext: SidebarEntryViewModel entry } || ViewModel is not { } vm)
            return;

        e.Handled = true;
        ClearDropTargets(vm);

        var external = FileDropHelper.GetPayload(e.DataTransfer) is null;
        try
        {
            var sources = await FileDropHelper.ResolveSourcesAsync(e.DataTransfer);
            if (sources.Count == 0)
                return;

            if (entry.Kind == SidebarEntryKind.Trash)
            {
                vm.MoveDroppedSourcesToTrash(sources);
                return;
            }

            var target = await ResolveTargetAsync(vm, entry);
            if (target is null)
                return;

            var same = !external &&
                       sources.All(s => string.Equals(s.FileSystem.Id, target.Value.FileSystem.Id, StringComparison.Ordinal));
            var move = TransferModifiers.Resolve(
                e.KeyModifiers.HasFlag(KeyModifiers.Control),
                e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                same) == TransferMode.Move;
            vm.EnqueueDropTransfer(sources, target.Value.FileSystem, target.Value.Path, move);
        }
        catch (Exception ex)
        {
            vm.AddToast(L10n.T("拖放失败"), ex.Message, ToastKind.Error);
        }
    }

    private static async Task<(IFileSystem FileSystem, string Path)?> ResolveTargetAsync(
        MainViewModel vm, SidebarEntryViewModel entry)
    {
        if (string.IsNullOrEmpty(entry.Path))
            return null;

        if (entry.FileSystemId is { Length: > 0 } id && id is not ("local" or "trash"))
        {
            try
            {
                var fileSystem = await vm.Services.FileSystems.ConnectByIdAsync(id).ConfigureAwait(true);
                return (fileSystem, entry.Path);
            }
            catch (Exception ex)
            {
                vm.AddToast(L10n.T("连接失败"), ex.Message, ToastKind.Error);
                return null;
            }
        }

        return (vm.Services.FileSystems.Local, entry.Path);
    }

    private static void ClearDropTargets(MainViewModel vm)
    {
        foreach (var item in vm.SidebarSections.SelectMany(section => section.Items))
        {
            if (item.IsDropTarget)
                item.IsDropTarget = false;
        }
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
