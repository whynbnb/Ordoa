using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ordoa.App.Services;
using Ordoa.App.ViewModels;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;
using Ordoa.Core.Localization;

namespace Ordoa.App.Views;

public partial class PaneView : UserControl
{
    private PaneViewModel? _pane;
    private PointerPressedEventArgs? _dragTrigger;
    private Point _dragStart;
    private FileItemViewModel? _dragItem;
    private IReadOnlyList<FileItemViewModel>? _dragSelection;
    private bool _dragging;
    private readonly DispatcherTimer _dropClearTimer;
    private TopLevel? _dropKeyTopLevel;
    private IReadOnlyList<OperationSource>? _dropSources;
    private string? _dropDestination;
    private string? _dropDestinationLabel;
    private bool _dropExternal;
    private bool _dropTrash;
    private bool _marqueePending;
    private bool _marqueeActive;
    private bool _marqueeAdditive;
    private Point _marqueeStart;
    private ListBox? _marqueeList;

    public PaneView()
    {
        InitializeComponent();

        // DragLeave is occasionally raised for a frame while the pointer is
        // still over the list. Delay clearing the overlay so crossing child
        // visuals does not make the drop hint flash off and on.
        _dropClearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _dropClearTimer.Tick += (_, _) =>
        {
            _dropClearTimer.Stop();
            ClearDropContext();
            _pane?.SetDropTarget(false);
        };

        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;

        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) =>
        {
            UpdateNarrowState();
            ScrollBreadcrumbToEnd();
        };
        AddHandler(PointerReleasedEvent, OnAnyPointerReleased, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerPressedEvent, OnListPointerPressedTunnel, RoutingStrategies.Tunnel);
        FileGrid.AddHandler(PointerPressedEvent, OnListPointerPressedTunnel, RoutingStrategies.Tunnel);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _dropKeyTopLevel = TopLevel.GetTopLevel(this);
        if (_dropKeyTopLevel is not null)
        {
            _dropKeyTopLevel.AddHandler(KeyDownEvent, OnDropModifierChanged,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            _dropKeyTopLevel.AddHandler(KeyUpEvent, OnDropModifierChanged,
                RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_dropKeyTopLevel is not null)
        {
            _dropKeyTopLevel.RemoveHandler(KeyDownEvent, OnDropModifierChanged);
            _dropKeyTopLevel.RemoveHandler(KeyUpEvent, OnDropModifierChanged);
            _dropKeyTopLevel = null;
        }

        _dropClearTimer.Stop();
        ClearDropContext();
    }

    private void OnDropModifierChanged(object? sender, KeyEventArgs e)
    {
        if (_pane is null || _dropSources is null)
            return;

        if (_dropTrash)
        {
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var canTrash = _dropExternal
                ? shift || _pane.Services.Trash.IsSupported
                : shift
                    ? _dropSources.Count > 0 &&
                      _dropSources.All(s => s.FileSystem is not TrashFileSystem)
                    : _pane.Services.Trash.IsSupported && FileDropHelper.IsTrashable(_dropSources);
            if (!canTrash)
            {
                ClearDropContext();
                _pane.SetDropTarget(false);
                return;
            }

            _pane.SetDropTarget(true, destination: L10n.T(shift ? "永久删除" : "回收站"),
                trash: true, permanent: shift);
            return;
        }

        if (_dropDestination is null)
            return;

        var move = FileDropHelper.IsMove(e.KeyModifiers, _dropSources, _pane.FileSystem.Id, _dropExternal);
        if (move && FileDropHelper.IsNoOpMove(_dropSources, _pane.FileSystem, _dropDestination))
        {
            _pane.SetDropTarget(false);
            return;
        }
        _pane.SetDropTarget(true, move, _dropDestinationLabel ?? _dropDestination);
    }

    private void ClearDropContext()
    {
        _dropSources = null;
        _dropDestination = null;
        _dropDestinationLabel = null;
        _dropExternal = false;
        _dropTrash = false;
    }

    /// <summary>
    /// Avalonia collapses a multi-selection as soon as one of its items is
    /// pressed. Snapshot the selection before that happens so a group drag can
    /// restore and carry all selected files.
    /// </summary>
    private void OnListPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        _dragSelection = null;
        if (_pane is null || sender is not ListBox list)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (GetItemAt(e.Source) is not { } item)
            return;

        // ListBoxItem handles the bubbling press event itself, so start
        // tracking the drag from the tunnel handler before that happens.
        _dragTrigger = e;
        _dragStart = e.GetPosition(this);
        _dragItem = item;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;
        if (list.SelectedItems is not { Count: > 1 } selected || !selected.Contains(item))
            return;

        _dragSelection = selected.OfType<FileItemViewModel>().ToList();
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
        ApplyColumnDefinitions();
        ScrollBreadcrumbToEnd();
    }

    private void OnWideRowAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Grid grid && _pane is not null)
            grid.ColumnDefinitions = new ColumnDefinitions(_pane.WideColumnLayout);
    }

    private void ApplyColumnDefinitions()
    {
        if (_pane is null)
            return;

        WideHeader.ColumnDefinitions = new ColumnDefinitions(_pane.WideColumnLayout);
        foreach (var grid in FileList.GetVisualDescendants().OfType<Grid>())
        {
            if (grid.Classes.Contains("rowwide"))
                grid.ColumnDefinitions = new ColumnDefinitions(_pane.WideColumnLayout);
        }
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
        if (e.PropertyName == nameof(PaneViewModel.WideColumnLayout))
            ApplyColumnDefinitions();
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

    /// <summary>
    /// Drop targets are the ListBox itself, so resolve the row under the
    /// pointer with a hit test instead of looking at the event source.
    /// </summary>
    private static FileItemViewModel? GetItemAtPoint(ListBox list, Point point)
        => GetItemAt(list.InputHitTest(point));

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
            return;
        }

        if (point.Properties.IsLeftButtonPressed && item is null && list is not null &&
            !IsScrollBarSource(e.Source))
        {
            BeginMarquee(list, e.GetPosition(list), e.KeyModifiers, e.Pointer);
        }
    }

    private static bool IsScrollBarSource(object? source)
        => source is Visual visual &&
           visual.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null;

    private void BeginMarquee(ListBox list, Point start, KeyModifiers modifiers, IPointer? pointer)
    {
        _marqueePending = true;
        _marqueeActive = false;
        _marqueeList = list;
        _marqueeStart = start;
        _marqueeAdditive = modifiers.HasFlag(KeyModifiers.Control) ||
                           modifiers.HasFlag(KeyModifiers.Shift);
        if (!_marqueeAdditive)
            list.Selection.Clear();
        list.Focus();
        pointer?.Capture(list);
    }

    private void UpdateMarquee(Point current)
    {
        var list = _marqueeList;
        if (list is null)
            return;

        if (!_marqueeActive)
        {
            if (Math.Abs(current.X - _marqueeStart.X) < 4 &&
                Math.Abs(current.Y - _marqueeStart.Y) < 4)
                return;
            _marqueeActive = true;
            MarqueeLayer.IsVisible = true;
        }

        var x1 = Math.Min(_marqueeStart.X, current.X);
        var y1 = Math.Min(_marqueeStart.Y, current.Y);
        var x2 = Math.Max(_marqueeStart.X, current.X);
        var y2 = Math.Max(_marqueeStart.Y, current.Y);
        var rect = new Rect(x1, y1, x2 - x1, y2 - y1).Intersect(new Rect(list.Bounds.Size));

        var topLeft = list.TranslatePoint(rect.TopLeft, MarqueeLayer);
        if (topLeft is not null)
        {
            Canvas.SetLeft(MarqueeRect, topLeft.Value.X);
            Canvas.SetTop(MarqueeRect, topLeft.Value.Y);
        }
        MarqueeRect.Width = Math.Max(0, rect.Width);
        MarqueeRect.Height = Math.Max(0, rect.Height);

        var indexes = new List<int>();
        foreach (var container in list.GetRealizedContainers().OfType<ListBoxItem>())
        {
            var index = list.IndexFromContainer(container);
            if (index < 0)
                continue;
            var origin = container.TranslatePoint(new Point(0, 0), list);
            if (origin is null)
                continue;
            if (new Rect(origin.Value, container.Bounds.Size).Intersects(rect))
                indexes.Add(index);
        }
        indexes.Sort();

        using (list.Selection.BatchUpdate())
        {
            if (!_marqueeAdditive)
                list.Selection.Clear();
            foreach (var index in indexes)
                list.Selection.Select(index);
            if (indexes.Count > 0)
                list.Selection.AnchorIndex = indexes[^1];
        }
    }

    private void EndMarquee(IPointer? pointer)
    {
        if (!_marqueePending && !_marqueeActive)
            return;

        _marqueePending = false;
        _marqueeActive = false;
        MarqueeLayer.IsVisible = false;
        _marqueeList = null;
        pointer?.Capture(null);
    }

    private void OnFilePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_marqueePending)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                UpdateMarquee(e.GetPosition(_marqueeList));
                return;
            }

            EndMarquee(e.Pointer);
            return;
        }

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

        // The pressed item may have been toggled off with Ctrl before the
        // pointer moved; never drag a deselected item.
        if (_pane.SelectedItems.Count == 0 || !_pane.SelectedItems.Contains(_dragItem))
        {
            _dragSelection = null;
            _dragTrigger = null;
            _dragItem = null;
            return;
        }

        _dragging = true;
        var trigger = _dragTrigger;
        var item = _dragItem;
        var group = _dragSelection;
        _dragSelection = null;
        _dragTrigger = null;
        _dragItem = null;
        _ = StartDragAsync(trigger, item, group);
    }

    private async Task StartDragAsync(PointerPressedEventArgs trigger, FileItemViewModel item,
        IReadOnlyList<FileItemViewModel>? groupSelection)
    {
        try
        {
            if (_pane is null)
                return;

            FileDragContext.BeginDrag();
            CloseOpenToolTips(TopLevel.GetTopLevel(this));

            if (groupSelection is { Count: > 0 })
                RestoreSelection(groupSelection);

            var sources = groupSelection is { Count: > 0 }
                ? groupSelection.Select(i => new OperationSource(_pane.FileSystem, i.Entry)).ToList()
                : _pane.SelectedItems.Count > 0 && _pane.SelectedItems.Contains(item)
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
            FileDragContext.EndDrag();
            FileDragContext.Current = null;
        }
    }

    private static void CloseOpenToolTips(TopLevel? topLevel)
    {
        if (topLevel is not Control root)
            return;

        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (ToolTip.GetIsOpen(control))
                ToolTip.SetIsOpen(control, false);
        }
    }

    private void OnAnyPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndMarquee(e.Pointer);
        _dragSelection = null;
        _dragTrigger = null;
        _dragItem = null;
    }

    private void RestoreSelection(IReadOnlyList<FileItemViewModel> selection)
    {
        var list = GetActiveList();
        if (list is null)
            return;

        var indexes = selection.Select(item => list.Items.IndexOf(item)).Where(index => index >= 0).ToList();
        if (indexes.Count == 0)
            return;

        using (list.Selection.BatchUpdate())
        {
            list.Selection.Clear();
            foreach (var index in indexes)
                list.Selection.Select(index);
            list.Selection.AnchorIndex = indexes[^1];
        }
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        _dropClearTimer.Stop();

        if (_pane is null)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropContext();
            return;
        }

        var payload = FileDropHelper.GetPayload(e.DataTransfer);
        var sources = payload?.Sources;
        if (!FileDropHelper.HasData(e.DataTransfer, payload))
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropContext();
            _pane.SetDropTarget(false);
            return;
        }

        if (_pane.IsTrash)
        {
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var canTrash = payload is null
                ? shift || _pane.Services.Trash.IsSupported
                : shift
                    ? sources is { Count: > 0 } &&
                      sources.All(s => s.FileSystem is not TrashFileSystem)
                    : _pane.Services.Trash.IsSupported && FileDropHelper.IsTrashable(sources);
            if (!canTrash)
            {
                e.DragEffects = DragDropEffects.None;
                ClearDropContext();
                _pane.SetDropTarget(false);
                return;
            }

            _dropSources = sources ?? [];
            _dropExternal = payload is null;
            _dropTrash = true;
            _dropDestination = null;
            _dropDestinationLabel = null;
            e.DragEffects = DragDropEffects.Move;
            _pane.SetDropTarget(true, destination: L10n.T(shift ? "永久删除" : "回收站"),
                trash: true, permanent: shift);
            e.Handled = true;
            return;
        }

        var list = sender as ListBox ?? GetActiveList();
        var target = list is null ? null : GetItemAtPoint(list, e.GetPosition(list));
        var destination = target is { IsDirectory: true } ? target.Path : _pane.CurrentPath;
        var destinationLabel = target is { IsDirectory: true }
            ? _pane.FileSystem.GetDisplayPath(destination)
            : _pane.DisplayPath;
        var move = FileDropHelper.IsMove(e.KeyModifiers, sources, _pane.FileSystem.Id, payload is null);

        if (move && FileDropHelper.IsNoOpMove(sources ?? [], _pane.FileSystem, destination))
        {
            ClearDropContext();
            _pane.SetDropTarget(false);
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _dropSources = sources ?? [];
        _dropExternal = payload is null;
        _dropTrash = false;
        _dropDestination = destination;
        _dropDestinationLabel = destinationLabel;

        e.DragEffects = move ? DragDropEffects.Move : DragDropEffects.Copy;
        _pane.SetDropTarget(true, move, destinationLabel);
        e.Handled = true;
    }

    private void OnFileDragLeave(object? sender, DragEventArgs e)
    {
        if (_pane is null)
            return;

        _dropClearTimer.Stop();

        // Avalonia can raise DragLeave while the pointer is still inside the
        // list (for example when crossing a child visual). Clearing the
        // overlay here causes a very visible off/on flicker; only clear when
        // the pointer actually leaves the list.
        if (sender is Control control)
        {
            var position = e.GetPosition(control);
            if (new Rect(control.Bounds.Size).Contains(position))
                return;
        }

        // A short delay absorbs hit-test transitions between list rows and
        // child visuals without leaving the overlay visible after a real exit.
        _dropClearTimer.Start();
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (_pane is null)
            return;

        _dropClearTimer.Stop();
        ClearDropContext();
        _pane.SetDropTarget(false);
        e.Handled = true;

        try
        {
            var sources = await FileDropHelper.ResolveSourcesAsync(e.DataTransfer);
            if (sources.Count == 0)
                return;

            if (_pane.IsTrash)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    await _pane.EnqueuePermanentDropAsync(sources);
                else
                    _pane.EnqueueTrashDrop(sources);
                return;
            }

            var list = sender as ListBox ?? GetActiveList();
            var target = list is null ? null : GetItemAtPoint(list, e.GetPosition(list));
            var destination = target is { IsDirectory: true } ? target.Path : _pane.CurrentPath;
            var move = FileDropHelper.IsMove(e.KeyModifiers, sources, _pane.FileSystem.Id,
                FileDropHelper.GetPayload(e.DataTransfer) is not { Sources.Count: > 0 });
            if (move && FileDropHelper.IsNoOpMove(sources, _pane.FileSystem, destination))
                return;
            await _pane.EnqueueDropTransferAsync(sources, destination, move);
        }
        catch (Exception ex)
        {
            Core.Services.Log.Warn("拖放失败", ex);
            _pane.Notify(L10n.T("拖放失败"), ex.Message, true);
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

    private async void OnToggleSearchScopeClick(object? sender, RoutedEventArgs e)
    {
        if (_pane is not null)
            await _pane.ToggleSearchScopeAsync();
    }

    private async void OnSaveSearchClick(object? sender, RoutedEventArgs e)
    {
        if (_pane is not null)
        {
            HideSearchFlyout();
            await _pane.SaveCurrentSearchAsync();
        }
    }

    private async void OnHistorySearchClick(object? sender, RoutedEventArgs e)
    {
        if (_pane is null || sender is not Control { Tag: string query } || string.IsNullOrWhiteSpace(query))
            return;
        HideSearchFlyout();
        await _pane.RunSearchAsync(query);
    }

    private void HideSearchFlyout()
    {
        if (Resources.TryGetResource("SearchFlyout", null, out var resource) &&
            resource is FlyoutBase flyout)
            flyout.Hide();
    }

    private void OnDeleteSavedSearchClick(object? sender, RoutedEventArgs e)
    {
        if (_pane is not null && sender is Control { Tag: SavedSearch search })
            _pane.RemoveSavedSearch(search);
    }

    private void OnClearSearchHistoryClick(object? sender, RoutedEventArgs e) => _pane?.ClearSearchHistory();

    private void OnNewFolderClick(object? sender, RoutedEventArgs e) => Run(pane => pane.NewFolderAsync());
    private void OnNewFileClick(object? sender, RoutedEventArgs e) => Run(pane => pane.NewFileAsync());
    private void OnPasteClick(object? sender, RoutedEventArgs e) => Run(pane => pane.PasteAsync());
    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => _pane?.RequestSelectAll();
    private void OnCompressZipClick(object? sender, RoutedEventArgs e) => Run(pane => pane.CompressAsync(false));
    private void OnCompressTarClick(object? sender, RoutedEventArgs e) => Run(pane => pane.CompressAsync(true));
    private void OnCompress7zClick(object? sender, RoutedEventArgs e) => Run(pane => pane.Compress7zAsync());
    private void OnExtractClick(object? sender, RoutedEventArgs e) => Run(pane => pane.ExtractAsync());
    private void OnChecksumClick(object? sender, RoutedEventArgs e) => Run(pane => pane.ChecksumAsync());
    private void OnDiskUsageClick(object? sender, RoutedEventArgs e) => Run(pane => pane.AnalyzeDiskUsageAsync());
    private void OnDuplicateFinderClick(object? sender, RoutedEventArgs e) => Run(pane => pane.FindDuplicateFilesAsync());
    private void OnGitStatusClick(object? sender, RoutedEventArgs e) => Run(pane => pane.ShowGitStatusAsync());
    private void OnOpenTerminalClick(object? sender, RoutedEventArgs e) => Run(pane => pane.OpenTerminalAsync());
    private void OnCopyPathClick(object? sender, RoutedEventArgs e) => Run(pane => pane.CopyPathAsync());
    private void OnPropertiesClick(object? sender, RoutedEventArgs e) => Run(pane => pane.PropertiesAsync());
    private void OnToggleViewClick(object? sender, RoutedEventArgs e) => _pane?.ToggleViewCommand.Execute(null);
    private void OnToggleTreeClick(object? sender, RoutedEventArgs e) => _pane?.ToggleTreeCommand.Execute(null);

    private async void OnTreeExpanderClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_pane is not null && sender is Control { DataContext: FileItemViewModel item })
            await _pane.ToggleTreeExpansionAsync(item);
    }
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

        menu.Items.Add(MenuItem(L10n.T("打开"), () => pane.OpenAsync(), pane.HasSelection));
        menu.Items.Add(MenuItem(L10n.T("以压缩包浏览"), () => pane.OpenArchiveAsync(), pane.CanOpenArchive));
        menu.Items.Add(MenuItem(L10n.T("打开方式"), pane.OpenWithAsync, pane.CanOpenWith));
        menu.Items.Add(MenuItem(L10n.T("快速预览"), () =>
        {
            pane.RequestPreview();
            return Task.CompletedTask;
        }, pane.HasSelection));
        menu.Items.Add(MenuItem(L10n.T("在新标签页中打开"), () => pane.OpenSelectionInNewTabAsync(), pane.HasSelection));
        menu.Items.Add(MenuItem(L10n.T("在新窗口打开"), pane.OpenInNewWindowAsync, pane.CanOpenInNewWindow));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(L10n.T("剪切"), () => pane.CopySelectionAsync(true), pane.CanCopy));
        menu.Items.Add(MenuItem(L10n.T("复制"), () => pane.CopySelectionAsync(false), pane.CanCopy));
        menu.Items.Add(MenuItem(L10n.T("粘贴"), pane.PasteAsync, pane.CanPaste));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(L10n.T("重命名"), pane.RenameAsync, pane.CanRename));
        menu.Items.Add(MenuItem(L10n.T("批量重命名"), pane.BatchRenameAsync, pane.CanBatchRename));
        var linkMenu = new MenuItem { Header = L10n.T("创建链接") };
        linkMenu.Items.Add(MenuItem(L10n.T("符号链接"), () => pane.CreateSymbolicLinkAsync(false),
            pane.CanCreateLink));
        linkMenu.Items.Add(MenuItem(L10n.T("相对符号链接"), () => pane.CreateSymbolicLinkAsync(true),
            pane.CanCreateLink));
        linkMenu.Items.Add(MenuItem(L10n.T("硬链接"), pane.CreateHardLinkAsync,
            pane.CanCreateLink && pane.PrimarySelection is { IsDirectory: false }));
        menu.Items.Add(linkMenu);

        if (pane.IsTrash)
        {
            menu.Items.Add(MenuItem(L10n.T("恢复"), pane.RestoreSelectionAsync, pane.CanRestore));
            menu.Items.Add(MenuItem(L10n.T("永久删除"), () => pane.DeleteSelectionAsync(true), pane.CanDelete));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(L10n.T("清空回收站"), pane.EmptyTrashAsync, pane.CanEmptyTrash));
        }
        else
        {
            var label = pane.FileSystem.IsLocal ? L10n.T("移到回收站") : L10n.T("永久删除");
            menu.Items.Add(MenuItem(label, () => pane.DeleteSelectionAsync(!pane.FileSystem.IsLocal), pane.CanDelete));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(L10n.T("压缩为 ZIP"), () => pane.CompressAsync(false), pane.CanCompress));
        menu.Items.Add(MenuItem(L10n.T("压缩为 tar.gz"), () => pane.CompressAsync(true), pane.CanCompress));
        menu.Items.Add(MenuItem(L10n.T("解压到此处"), pane.ExtractAsync, pane.CanExtract));
        menu.Items.Add(MenuItem(L10n.T("解压到…"), pane.ExtractSelectionToAsync, pane.CanExtractTo));
        menu.Items.Add(MenuItem(L10n.T("计算 SHA-256"), pane.ChecksumAsync, pane.CanChecksum));
        menu.Items.Add(MenuItem(L10n.T("计算文件夹大小"), pane.CalculateFolderSizesAsync,
            pane.CanCalculateFolderSize));
        var analysisMenu = new MenuItem { Header = L10n.T("分析工具") };
        analysisMenu.Items.Add(MenuItem(L10n.T("磁盘占用分析"), pane.AnalyzeDiskUsageAsync, pane.CanAnalyze));
        analysisMenu.Items.Add(MenuItem(L10n.T("查找重复文件"), pane.FindDuplicateFilesAsync, pane.CanAnalyze));
        menu.Items.Add(analysisMenu);
        menu.Items.Add(MenuItem(L10n.T("Git 状态"), pane.ShowGitStatusAsync, pane.CanShowGitStatus));
        menu.Items.Add(MenuItem(L10n.T("比较选中文件"), pane.CompareSelectionAsync, pane.CanCompare));
        menu.Items.Add(new Separator());
        AddTerminalMenuItems(menu, pane);
        AddCustomCommandMenuItems(menu, pane);
        menu.Items.Add(MenuItem(L10n.T("复制路径"), pane.CopyPathAsync, true));
        menu.Items.Add(MenuItem(L10n.T("复制名称"), pane.CopyNameAsync, pane.HasSelection));
        menu.Items.Add(MenuItem(L10n.T("属性"), pane.PropertiesAsync, true));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(L10n.T("新建文件夹"), pane.NewFolderAsync, pane.CanCreateFolder));
        menu.Items.Add(MenuItem(L10n.T("新建空文件"), pane.NewFileAsync, pane.CanCreateFile));
        menu.Items.Add(MenuItem(L10n.T("全选"), () =>
        {
            pane.RequestSelectAll();
            return Task.CompletedTask;
        }, pane.CanSelectAll));
        menu.Items.Add(MenuItem(L10n.T("刷新"), pane.RefreshAsync, true));

        host.ContextMenu = menu;
        menu.Open(host);
        e.Handled = true;
    }

    private void AddTerminalMenuItems(ContextMenu menu, PaneViewModel pane)
    {
        var contextTerminals = pane.ContextTerminals;
        var defaultTerminal = pane.DefaultContextTerminal;
        if (defaultTerminal is not null)
        {
            menu.Items.Add(MenuItem(L10n.Format("在 {0} 中打开", defaultTerminal.Name),
                () => pane.OpenTerminalAsync(defaultTerminal.Id), pane.CanOpenTerminal));
        }

        var alternates = contextTerminals
            .Where(terminal => !string.Equals(terminal.Id, defaultTerminal?.Id, StringComparison.Ordinal))
            .ToList();
        if (alternates.Count > 0)
        {
            var submenu = new MenuItem
            {
                Header = defaultTerminal is null
                    ? L10n.T("在终端中打开")
                    : L10n.T("使用其他终端打开"),
            };
            foreach (var terminal in alternates)
            {
                var id = terminal.Id;
                submenu.Items.Add(MenuItem(terminal.Name,
                    () => pane.OpenTerminalAsync(id), pane.CanOpenTerminal));
            }

            menu.Items.Add(submenu);
        }

        if (pane.EnabledHerdr is not null)
        {
            menu.Items.Add(MenuItem(L10n.T("在 Herdr 中打开"),
                pane.OpenInHerdrAsync, pane.CanOpenTerminal));
        }
    }

    private void AddCustomCommandMenuItems(ContextMenu menu, PaneViewModel pane)
    {
        var commands = pane.ContextCustomCommands;
        if (commands.Count == 0)
            return;

        var submenu = new MenuItem { Header = L10n.T("自定义命令") };
        foreach (var command in commands)
        {
            var id = command.Id;
            var item = new MenuItem
            {
                Header = command.Name,
                IsEnabled = pane.CanRunCustomCommand(command),
            };
            item.Click += (_, _) => Run(p => p.RunCustomCommandAsync(id));
            submenu.Items.Add(item);
        }

        menu.Items.Add(submenu);
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
            _pane.Notify(L10n.T("操作失败"), ex.Message, true);
        }
    }
}
