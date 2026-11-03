using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ordoa.App.Services;
using Ordoa.App.ViewModels;
using Ordoa.Core.Abstractions;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;

namespace Ordoa.App.Views;

public partial class SidebarView : UserControl
{
    private readonly DispatcherTimer _dropClearTimer;
    private TopLevel? _keyTopLevel;
    private SidebarEntryViewModel? _dropEntry;
    private SidebarEntryViewModel? _pendingClear;
    private FileDragPayload? _dropPayload;
    private bool _dropExternal;
    private bool? _dragCursorMove;

    public SidebarView()
    {
        InitializeComponent();

        // DragLeave can fire while the pointer is still inside an entry.
        // Delay clearing so moving within an entry does not flash the overlay.
        _dropClearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _dropClearTimer.Tick += (_, _) =>
        {
            _dropClearTimer.Stop();
            var entry = _pendingClear;
            _pendingClear = null;
            if (entry is null)
                return;

            if (!ReferenceEquals(entry, _dropEntry) || entry.IsDropTarget)
                entry.SetDropTarget(false);
            if (ReferenceEquals(entry, _dropEntry))
            {
                _dropEntry = null;
                if (!FileDragContext.IsDragging)
                    RestoreDragCursor();
            }
        };

        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        FileDragContext.DragEnded += OnDragEnded;
        _keyTopLevel = TopLevel.GetTopLevel(this);
        if (_keyTopLevel is not null)
        {
            _keyTopLevel.AddHandler(KeyDownEvent, OnDropModifierChanged,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            _keyTopLevel.AddHandler(KeyUpEvent, OnDropModifierChanged,
                RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        FileDragContext.DragEnded -= OnDragEnded;
        if (_keyTopLevel is not null)
        {
            _keyTopLevel.RemoveHandler(KeyDownEvent, OnDropModifierChanged);
            _keyTopLevel.RemoveHandler(KeyUpEvent, OnDropModifierChanged);
            _keyTopLevel = null;
        }
        _dropClearTimer.Stop();
        _dropEntry = null;
        _pendingClear = null;
        _dropPayload = null;
        RestoreDragCursor();
    }

    private void OnDragEnded()
    {
        RestoreDragCursor();
    }

    private void OnDropModifierChanged(object? sender, KeyEventArgs e)
    {
        if (_dropEntry is not { } entry || ViewModel is not { } vm)
            return;

        if (!CanDrop(entry, _dropPayload?.Sources, _dropExternal,
                e.KeyModifiers.HasFlag(KeyModifiers.Shift), vm))
        {
            ClearDropTargets(vm);
            _dropEntry = null;
            if (!FileDragContext.IsDragging)
                RestoreDragCursor();
            return;
        }

        var sources = _dropPayload?.Sources;
        var move = entry.Kind == SidebarEntryKind.Trash ||
                   FileDropHelper.IsMove(e.KeyModifiers, sources, TargetFileSystemId(entry), _dropExternal);
        if (move && IsNoOpSidebarMove(vm, entry, sources))
        {
            ClearDropTargets(vm);
            _dropEntry = null;
            if (!FileDragContext.IsDragging)
                RestoreDragCursor();
            return;
        }

        var appliedMove = ApplyDropTarget(vm, entry, _dropPayload, _dropExternal, e.KeyModifiers);
        SetDragCursor(appliedMove);
    }

    private void OnSidebarHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
            window.HandleTitleBarPointerPressed(sender, e);
    }

    private async void OnSectionHeaderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SidebarSectionViewModel section } ||
            section.HeaderAction is null)
            return;

        try
        {
            await section.HeaderAction();
        }
        catch (Exception ex)
        {
            ViewModel?.AddToast(L10n.T("操作失败"), ex.Message, ToastKind.Error);
        }
    }

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

        vm.ClearPaneDropTargets();
        _dropClearTimer.Stop();
        _pendingClear = null;

        var payload = FileDropHelper.GetPayload(e.DataTransfer);
        var sources = payload?.Sources;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!FileDropHelper.HasData(e.DataTransfer, payload) ||
            !CanDrop(entry, sources, payload is null, shift, vm))
        {
            ClearDropTargets(vm);
            _dropEntry = null;
            _dropPayload = null;
            if (!FileDragContext.IsDragging)
                RestoreDragCursor();
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var move = entry.Kind == SidebarEntryKind.Trash ||
                   FileDropHelper.IsMove(e.KeyModifiers, sources, TargetFileSystemId(entry), payload is null);
        if (move && IsNoOpSidebarMove(vm, entry, sources))
        {
            ClearDropTargets(vm);
            _dropEntry = null;
            _dropPayload = null;
            if (!FileDragContext.IsDragging)
                RestoreDragCursor();
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _dropEntry = entry;
        _dropPayload = payload;
        _dropExternal = payload is null;

        var appliedMove = ApplyDropTarget(vm, entry, payload, _dropExternal, e.KeyModifiers);
        SetDragCursor(appliedMove);
        e.DragEffects = appliedMove ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void SetDragCursor(bool move)
    {
        if (_dragCursorMove == move)
            return;

        _dragCursorMove = move;
        var cursor = new Cursor(move ? StandardCursorType.DragMove : StandardCursorType.DragCopy);
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
            button.Cursor = cursor;
    }

    private void RestoreDragCursor()
    {
        if (_dragCursorMove is null)
            return;

        _dragCursorMove = null;
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
            button.ClearValue(InputElement.CursorProperty);
    }

    private static bool IsNoOpSidebarMove(MainViewModel vm, SidebarEntryViewModel entry,
        IReadOnlyList<OperationSource>? sources)
    {
        if (entry.Kind == SidebarEntryKind.Trash || sources is not { Count: > 0 } ||
            string.IsNullOrEmpty(entry.Path))
            return false;

        IFileSystem? fileSystem = null;
        if (entry.FileSystemId is null or "local")
        {
            if (!sources.All(source => source.FileSystem.Id == "local"))
                return false;
            fileSystem = vm.Services.FileSystems.Local;
        }
        else
        {
            if (!sources.All(source => source.FileSystem.Id == entry.FileSystemId))
                return false;
            fileSystem = sources[0].FileSystem;
        }

        return FileDropHelper.IsNoOpMove(sources, fileSystem, entry.Path);
    }

    private static bool ApplyDropTarget(MainViewModel vm, SidebarEntryViewModel entry,
        FileDragPayload? payload, bool external, KeyModifiers modifiers)
    {
        var sources = payload?.Sources;
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var move = entry.Kind == SidebarEntryKind.Trash ||
                   FileDropHelper.IsMove(modifiers, sources, TargetFileSystemId(entry), external);

        ClearDropTargets(vm, entry);
        if (entry.Kind == SidebarEntryKind.Trash)
        {
            var permanent = shift;
            entry.SetDropTarget(true,
                permanent ? L10n.T("永久删除") : L10n.T("删除"),
                permanent ? "IconDelete" : "IconTrash",
                destructive: true);
        }
        else
        {
            entry.SetDropTarget(true,
                move ? L10n.T("移动到此文件夹") : L10n.T("复制到此文件夹"),
                move ? "IconFolder" : "IconCopy");
        }

        return move;
    }

    private static bool CanDrop(SidebarEntryViewModel entry, IReadOnlyList<OperationSource>? sources,
        bool external, bool permanent, MainViewModel vm)
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
                if (external)
                    return permanent || vm.Services.Trash.IsSupported;
                if (sources is not { Count: > 0 })
                    return false;
                // Shift+drag deletes permanently and also works for remote files.
                return permanent
                    ? sources.All(s => s.FileSystem is not TrashFileSystem)
                    : vm.Services.Trash.IsSupported && FileDropHelper.IsTrashable(sources);
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
        if (sender is not Button { DataContext: SidebarEntryViewModel entry } button)
            return;

        // Spurious DragLeave events can fire while the pointer is still over
        // the entry, especially when crossing its child visuals.
        var position = e.GetPosition(button);
        if (new Rect(button.Bounds.Size).Contains(position))
            return;

        _pendingClear = entry;
        _dropClearTimer.Stop();
        _dropClearTimer.Start();
    }

    private async void OnEntryDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Button { DataContext: SidebarEntryViewModel entry } || ViewModel is not { } vm)
            return;

        e.Handled = true;
        ClearDropTargets(vm);
        _dropClearTimer.Stop();
        _pendingClear = null;
        _dropEntry = null;
        _dropPayload = null;
        RestoreDragCursor();

        var external = FileDropHelper.GetPayload(e.DataTransfer) is null;
        try
        {
            var sources = await FileDropHelper.ResolveSourcesAsync(e.DataTransfer);
            if (sources.Count == 0)
                return;

            if (entry.Kind == SidebarEntryKind.Trash)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    await vm.DeleteDroppedSourcesPermanentlyAsync(sources);
                else
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
            if (move && FileDropHelper.IsNoOpMove(sources, target.Value.FileSystem, target.Value.Path))
                return;
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

    private static void ClearDropTargets(MainViewModel vm, SidebarEntryViewModel? except = null)
    {
        var sections = vm.SidebarSections.AsEnumerable();
        if (vm.SystemSection is not null)
            sections = sections.Append(vm.SystemSection);

        foreach (var item in sections.SelectMany(section => section.Items))
        {
            if (!ReferenceEquals(item, except) && item.IsDropTarget)
                item.SetDropTarget(false);
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
