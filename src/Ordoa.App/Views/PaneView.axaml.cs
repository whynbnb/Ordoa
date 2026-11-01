using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ordoa.App.Services;
using Ordoa.App.ViewModels;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.Views;

public partial class PaneView : UserControl
{
    private PaneViewModel? _pane;
    private PointerPressedEventArgs? _dragTrigger;
    private Point _dragStart;
    private FileItemViewModel? _dragItem;
    private bool _dragging;

    public PaneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) =>
        {
            UpdateNarrowState();
            ScrollBreadcrumbToEnd();
        };
        AddHandler(PointerReleasedEvent, OnAnyPointerReleased, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_pane is not null)
        {
            _pane.FocusSearchRequested -= OnFocusSearchRequested;
            _pane.FocusLocationRequested -= OnFocusLocationRequested;
            _pane.SelectAllRequested -= OnSelectAllRequested;
            _pane.PropertyChanged -= OnPanePropertyChanged;
        }

        _pane = DataContext as PaneViewModel;
        if (_pane is not null)
        {
            _pane.FocusSearchRequested += OnFocusSearchRequested;
            _pane.FocusLocationRequested += OnFocusLocationRequested;
            _pane.SelectAllRequested += OnSelectAllRequested;
            _pane.PropertyChanged += OnPanePropertyChanged;
        }
        UpdateNarrowState();
        ScrollBreadcrumbToEnd();
    }

    private void UpdateNarrowState()
    {
        if (_pane is null)
            return;
        _pane.IsNarrow = Bounds.Width is > 0 and < 560;
        if (_pane.IsNarrow)
        {
            if (!FileList.Classes.Contains("narrow"))
                FileList.Classes.Add("narrow");
        }
        else
        {
            FileList.Classes.Remove("narrow");
        }
    }

    private void OnPanePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneViewModel.Breadcrumbs) or nameof(PaneViewModel.IsEditingLocation))
            ScrollBreadcrumbToEnd();
    }

    private void ScrollBreadcrumbToEnd()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var max = Math.Max(0, BreadcrumbScroller.Extent.Width - BreadcrumbScroller.Viewport.Width);
            if (Math.Abs(BreadcrumbScroller.Offset.X - max) > 0.5)
                BreadcrumbScroller.Offset = new Vector(max, 0);
        }, DispatcherPriority.Background);
    }

    private void OnFocusSearchRequested(PaneViewModel pane)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnFocusLocationRequested(PaneViewModel pane)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LocationBox.Focus();
            LocationBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnSelectAllRequested(PaneViewModel pane)
    {
        GetActiveList()?.SelectAll();
    }

    private ListBox? GetActiveList() => _pane is null ? null : _pane.IsGrid ? FileGrid : FileList;

    private static FileItemViewModel? GetItemAt(object? source)
    {
        if (source is not Visual visual)
            return null;
        var container = visual.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        return container?.DataContext as FileItemViewModel;
    }

    private void OnFilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_pane is null)
            return;

        var item = GetItemAt(e.Source);
        var point = e.GetCurrentPoint(this);
        var list = GetActiveList();

        if (point.Properties.IsRightButtonPressed)
        {
            if (item is not null)
            {
                if (list?.SelectedItems is { } selected && !selected.Contains(item))
                {
                    list.UnselectAll();
                    list.SelectedItem = item;
                }
            }
            else
            {
                list?.UnselectAll();
            }
            return;
        }

        if (point.Properties.IsMiddleButtonPressed && item is not null)
        {
            _ = _pane.OpenSelectionInNewTabAsync(item);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && item is not null)
        {
            _dragTrigger = e;
            _dragStart = e.GetPosition(this);
            _dragItem = item;
        }
    }

    private void OnFilePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pane is null || _dragTrigger is null || _dragItem is null || _dragging)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragTrigger = null;
            _dragItem = null;
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < 7 && Math.Abs(position.Y - _dragStart.Y) < 7)
            return;

        _dragging = true;
        var trigger = _dragTrigger;
        var item = _dragItem;
        _dragTrigger = null;
        _dragItem = null;
        _ = StartDragAsync(trigger, item);
    }

    private async Task StartDragAsync(PointerPressedEventArgs trigger, FileItemViewModel item)
    {
        try
        {
            if (_pane is null)
                return;

            var sources = _pane.SelectedItems.Count > 0 && _pane.SelectedItems.Contains(item)
                ? _pane.SelectedItems.Select(i => new OperationSource(_pane.FileSystem, i.Entry)).ToList()
                : [new OperationSource(_pane.FileSystem, item.Entry)];

            var payload = new FileDragPayload(sources);
            FileDragContext.Current = payload;

            var transfer = new DataTransfer();
            var itemData = new DataTransferItem();
            itemData.SetText(string.Join(Environment.NewLine,
                sources.Select(s => s.FileSystem.GetDisplayPath(s.Path))));
            itemData.Set(FileDragContext.Format, payload);
            transfer.Add(itemData);

            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storageProvider is not null)
            {
                foreach (var source in sources.Where(s => s.FileSystem.IsLocal))
                {
                    try
                    {
                        IStorageItem? storageItem = source.IsDirectory
                            ? await storageProvider.TryGetFolderFromPathAsync(source.Path)
                            : await storageProvider.TryGetFileFromPathAsync(source.Path);
                        if (storageItem is not null)
                            transfer.Add(DataTransferItem.CreateFile(storageItem));
                    }
                    catch { /* item disappeared */ }
                }
            }

            await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Core.Services.Log.Warn("启动拖放失败", ex);
        }
        finally
        {
            _dragging = false;
            FileDragContext.Current = null;
        }
    }

    private void OnAnyPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragTrigger = null;
        _dragItem = null;
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (_pane is null || _pane.IsTrash)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var payload = e.DataTransfer.TryGetValue(FileDragContext.Format) ?? FileDragContext.Current;
        var hasData = payload is { Sources.Count: > 0 } ||
                      e.DataTransfer.Contains(DataFormat.File) ||
                      e.DataTransfer.Contains(DataFormat.Text);
        if (!hasData)
        {
            e.DragEffects = DragDropEffects.None;
            _pane.SetDropTarget(false);
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var move = payload is not null && !ctrl &&
                   payload.Sources.All(s => s.FileSystem.Id == _pane.FileSystem.Id);
        e.DragEffects = move ? DragDropEffects.Move : DragDropEffects.Copy;
        _pane.SetDropTarget(true);
        e.Handled = true;
    }

    private void OnFileDragLeave(object? sender, DragEventArgs e)
    {
        _pane?.SetDropTarget(false);
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (_pane is null)
            return;

        _pane.SetDropTarget(false);
        e.Handled = true;
        if (_pane.IsTrash)
            return;

        try
        {
            var targetItem = GetItemAt(e.Source);
            var destination = targetItem is { IsDirectory: true } ? targetItem.Path : _pane.CurrentPath;
            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            var payload = e.DataTransfer.TryGetValue(FileDragContext.Format) ?? FileDragContext.Current;

            if (payload is { Sources.Count: > 0 })
            {
                var move = !ctrl && payload.Sources.All(s => s.FileSystem.Id == _pane.FileSystem.Id);
                _pane.EnqueueDropTransfer(payload.Sources, destination, move);
                return;
            }

            var sources = new List<OperationSource>();
            var files = e.DataTransfer.TryGetFiles();
            if (files is not null)
            {
                foreach (var file in files)
                {
                    var path = file.TryGetLocalPath();
                    if (string.IsNullOrWhiteSpace(path))
                        continue;
                    var entry = await LocalFileSystem.Instance.GetEntryAsync(path);
                    if (entry is not null)
                        sources.Add(new OperationSource(LocalFileSystem.Instance, entry));
                }
            }

            if (sources.Count == 0)
            {
                var text = e.DataTransfer.TryGetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var path = line.Trim();
                        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                            Uri.TryCreate(path, UriKind.Absolute, out var uri))
                            path = uri.LocalPath;
                        if (!Path.IsPathRooted(path))
                            continue;
                        var entry = await LocalFileSystem.Instance.GetEntryAsync(path);
                        if (entry is not null)
                            sources.Add(new OperationSource(LocalFileSystem.Instance, entry));
                    }
                }
            }

            if (sources.Count > 0)
                _pane.EnqueueDropTransfer(sources, destination, false);
        }
        catch (Exception ex)
        {
            Core.Services.Log.Warn("拖放失败", ex);
            _pane.Notify("拖放失败", ex.Message, true);
        }
    }

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_pane is not null && sender is ListBox list)
            _pane.UpdateSelection(list.SelectedItems?.OfType<FileItemViewModel>() ?? []);
    }

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_pane is not null && sender is ListBox list)
            _pane.UpdateSelection(list.SelectedItems?.OfType<FileItemViewModel>() ?? []);
    }

    private async void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_pane is null)
            return;
        var item = GetItemAt(e.Source) ?? _pane.PrimarySelection;
        if (item is null)
            return;
        e.Handled = true;
        await _pane.OpenAsync(item);
    }

    private void OnSortHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (_pane is null || sender is not Button { Tag: string column })
            return;
        _pane.SortByCommand.Execute(column);
    }

    private async void OnBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (_pane is null || sender is not Button { DataContext: BreadcrumbItem item })
            return;
        await _pane.NavigateToBreadcrumbAsync(item);
    }

    private void OnBreadcrumbDoubleTapped(object? sender, TappedEventArgs e)
    {
        _pane?.BeginEditLocation();
    }

    private void OnEditPathClick(object? sender, RoutedEventArgs e) => _pane?.BeginEditLocation();
    private void OnCompactBreadcrumbClick(object? sender, RoutedEventArgs e) => _pane?.BeginEditLocation();

    private async void OnLocationKeyDown(object? sender, KeyEventArgs e)
    {
        if (_pane is null)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _pane.CommitLocationAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _pane.CancelEditLocation();
        }
    }

    private async void OnLocationLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_pane is { IsEditingLocation: true })
            await _pane.CommitLocationAsync();
    }

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_pane is null)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _pane.StartSearchAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_pane.IsSearchActive)
                _pane.ExitSearch();
            else
                _pane.FilterText = string.Empty;
        }
    }

    private void OnExitSearchClick(object? sender, RoutedEventArgs e) => _pane?.ExitSearch();

    private void OnNewFolderClick(object? sender, RoutedEventArgs e) => Run(pane => pane.NewFolderAsync());
    private void OnNewFileClick(object? sender, RoutedEventArgs e) => Run(pane => pane.NewFileAsync());
    private void OnPasteClick(object? sender, RoutedEventArgs e) => Run(pane => pane.PasteAsync());
    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => _pane?.RequestSelectAll();
    private void OnCompressZipClick(object? sender, RoutedEventArgs e) => Run(pane => pane.CompressAsync(false));
    private void OnCompressTarClick(object? sender, RoutedEventArgs e) => Run(pane => pane.CompressAsync(true));
    private void OnExtractClick(object? sender, RoutedEventArgs e) => Run(pane => pane.ExtractAsync());
    private void OnChecksumClick(object? sender, RoutedEventArgs e) => Run(pane => pane.ChecksumAsync());
    private void OnOpenTerminalClick(object? sender, RoutedEventArgs e) => Run(pane => pane.OpenTerminalAsync());
    private void OnCopyPathClick(object? sender, RoutedEventArgs e) => Run(pane => pane.CopyPathAsync());
    private void OnPropertiesClick(object? sender, RoutedEventArgs e) => Run(pane => pane.PropertiesAsync());
    private void OnToggleViewClick(object? sender, RoutedEventArgs e) => _pane?.ToggleViewCommand.Execute(null);
    private void OnToggleSplitClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PaneViewModel && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
            main.ToggleSplitCommand.Execute(null);
    }
    private void OnRefreshClick(object? sender, RoutedEventArgs e) => Run(pane => pane.RefreshAsync());
    private void OnRestoreClick(object? sender, RoutedEventArgs e) => Run(pane => pane.RestoreSelectionAsync());
    private void OnEmptyTrashClick(object? sender, RoutedEventArgs e) => Run(pane => pane.EmptyTrashAsync());
    private void OnSortNameClick(object? sender, RoutedEventArgs e) => _pane?.SortByCommand.Execute("Name");
    private void OnSortSizeClick(object? sender, RoutedEventArgs e) => _pane?.SortByCommand.Execute("Size");
    private void OnSortModifiedClick(object? sender, RoutedEventArgs e) => _pane?.SortByCommand.Execute("Modified");
    private void OnSortTypeClick(object? sender, RoutedEventArgs e) => _pane?.SortByCommand.Execute("Type");
    private void OnToggleFoldersFirstClick(object? sender, RoutedEventArgs e)
    {
        if (_pane is not null)
            _pane.FoldersFirst = !_pane.FoldersFirst;
    }

    private void OnFileContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_pane is null || sender is not Control host)
            return;

        var pane = _pane;
        var menu = new ContextMenu();

        menu.Items.Add(MenuItem("打开", () => pane.OpenAsync(), pane.HasSelection));
        menu.Items.Add(MenuItem("在新标签页中打开", () => pane.OpenSelectionInNewTabAsync(), pane.HasSelection));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("剪切", () => pane.CopySelectionAsync(true), pane.CanCopy));
        menu.Items.Add(MenuItem("复制", () => pane.CopySelectionAsync(false), pane.CanCopy));
        menu.Items.Add(MenuItem("粘贴", pane.PasteAsync, pane.CanPaste));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("重命名", pane.RenameAsync, pane.CanRename));

        if (pane.IsTrash)
        {
            menu.Items.Add(MenuItem("恢复", pane.RestoreSelectionAsync, pane.CanRestore));
            menu.Items.Add(MenuItem("永久删除", () => pane.DeleteSelectionAsync(true), pane.CanDelete));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("清空回收站", pane.EmptyTrashAsync, pane.CanEmptyTrash));
        }
        else
        {
            var label = pane.FileSystem.IsLocal ? "移到回收站" : "永久删除";
            menu.Items.Add(MenuItem(label, () => pane.DeleteSelectionAsync(!pane.FileSystem.IsLocal), pane.CanDelete));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("压缩为 ZIP", () => pane.CompressAsync(false), pane.CanCompress));
        menu.Items.Add(MenuItem("压缩为 tar.gz", () => pane.CompressAsync(true), pane.CanCompress));
        menu.Items.Add(MenuItem("解压到此处", pane.ExtractAsync, pane.CanExtract));
        menu.Items.Add(MenuItem("计算 SHA-256", pane.ChecksumAsync, pane.CanChecksum));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("在终端中打开", pane.OpenTerminalAsync, pane.CanOpenTerminal));
        menu.Items.Add(MenuItem("复制路径", pane.CopyPathAsync, true));
        menu.Items.Add(MenuItem("复制名称", pane.CopyNameAsync, pane.HasSelection));
        menu.Items.Add(MenuItem("属性", pane.PropertiesAsync, true));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("新建文件夹", pane.NewFolderAsync, pane.CanCreateFolder));
        menu.Items.Add(MenuItem("新建空文件", pane.NewFileAsync, pane.CanCreateFile));
        menu.Items.Add(MenuItem("全选", () =>
        {
            pane.RequestSelectAll();
            return Task.CompletedTask;
        }, pane.CanSelectAll));
        menu.Items.Add(MenuItem("刷新", pane.RefreshAsync, true));

        host.ContextMenu = menu;
        menu.Open(host);
        e.Handled = true;
    }

    private MenuItem MenuItem(string header, Func<Task> action, bool enabled)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => Run(pane => action());
        return item;
    }

    private async void Run(Func<PaneViewModel, Task> action)
    {
        if (_pane is null)
            return;
        try
        {
            await action(_pane);
        }
        catch (Exception ex)
        {
            Core.Services.Log.Error("文件面板操作失败", ex);
            _pane.Notify("操作失败", ex.Message, true);
        }
    }
}
