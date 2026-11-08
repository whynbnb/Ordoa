using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ordoa.App.Services;
using Ordoa.App.ViewModels;
using Ordoa.Core.Services;
using Ordoa.Core.Localization;

namespace Ordoa.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private bool _initialized;
    private bool _initialSizeApplied;
    private double _lastNormalWidth;
    private double _lastNormalHeight;
    private TabViewModel? _dragTab;
    private PointerPressedEventArgs? _tabDragTrigger;
    private Border? _dragTabBorder;
    private Point _tabDragStart;
    private bool _tabDragging;

    public MainWindow()
    {
        WindowAppearance.Apply(this, guardInitialKeys: false);
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closing += OnClosing;
        Resized += OnResized;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Mouse back/forward buttons navigate the pane under the pointer.</summary>
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || _viewModel.IsCommandPaletteOpen || _viewModel.Preview.IsOpen)
            return;

        var pane = _viewModel.ActivePane;
        if (e.Source is Visual visual &&
            visual.FindAncestorOfType<PaneView>(includeSelf: true)?.DataContext is PaneViewModel hovered &&
            !ReferenceEquals(hovered, pane))
        {
            _viewModel.ActiveTab?.ActivatePane(hovered);
            pane = hovered;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed)
        {
            if (pane is { CanGoBack: true })
            {
                pane.GoBackCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (properties.IsXButton2Pressed)
        {
            if (pane is { CanGoForward: true })
            {
                pane.GoForwardCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OnPreviewBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (e.Source is Visual visual)
        {
            for (var current = visual; current is not null; current = current.GetVisualParent())
            {
                if (ReferenceEquals(current, PreviewCard))
                    return;
            }
        }
        _viewModel.Preview.Close();
    }

    private void OnPreviewCloseClick(object? sender, RoutedEventArgs e) => _viewModel?.Preview.Close();

    /// <summary>
    /// Starts a native window move from the custom title bar. Interactive
    /// controls (tabs, buttons, text boxes) keep their normal behavior.
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        => HandleTitleBarPointerPressed(sender, e);

    internal void HandleTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || _viewModel is not { IsImmersiveWindow: true })
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsInteractiveSource(e.Source as Visual))
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private static bool IsInteractiveSource(Visual? source)
    {
        for (var current = source; current is not null; current = current.GetVisualParent())
        {
            if (current is Button or TextBox or ComboBox or ScrollBar or Slider or MenuItem or TabItem)
                return true;
            if (current is Border border && border.Classes.Contains("tab"))
                return true;
        }

        return false;
    }

    private void ToggleMaximizeRestore()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimizeWindowClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        e.Handled = true;
    }

    private void OnMaximizeRestoreWindowClick(object? sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
        e.Handled = true;
    }

    private void OnCloseWindowClick(object? sender, RoutedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void OnPreviewOpenClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            Run(_viewModel.OpenPreviewedAsync);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.CloseWindowRequested -= OnCloseWindowRequested;
            _viewModel.ActiveTabChanged -= OnActiveTabChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.CloseWindowRequested += OnCloseWindowRequested;
            _viewModel.ActiveTabChanged += OnActiveTabChanged;
            ApplySavedWindowSize();
        }
    }

    /// <summary>
    /// 在窗口首次显示前应用上次的尺寸。必须在 Show 之前设置：
    /// 平铺合成器（如 niri）会把映射后的 resize 当成窗口高度请求，
    /// 导致窗口缩在列的上半部分、下方留空。
    /// </summary>
    private void ApplySavedWindowSize()
    {
        if (_initialSizeApplied || _viewModel is null)
            return;
        _initialSizeApplied = true;

        var settings = _viewModel.Services.Settings.Current;
        var width = settings.WindowWidth;
        var height = settings.WindowHeight;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
            width = Math.Min(width, Math.Max(840, screen.WorkingArea.Width / scaling - 48));
            height = Math.Min(height, Math.Max(540, screen.WorkingArea.Height / scaling - 48));
        }

        Width = width;
        Height = height;
        _lastNormalWidth = width;
        _lastNormalHeight = height;
    }

    private void OnCloseWindowRequested() => Close();

    private void OnActiveTabChanged(TabViewModel? tab)
    {
        if (tab is null)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            var container = TabScroller.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("tab") && ReferenceEquals(b.DataContext, tab));
            container?.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_viewModel is null)
            return;

        var settings = _viewModel.Services.Settings.Current;
        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
        UpdateSidebarColumn();

        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("初始化失败", ex);
            _viewModel.EnsureInitialized();
            _viewModel.AddToast(L10n.T("启动时发生错误"), ex.Message, ToastKind.Error);
        }

        try
        {
            await _viewModel.ResumePendingTransfersAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("恢复传输队列失败", ex);
        }

        _initialized = true;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_viewModel is null)
            return;

        var maximized = WindowState == WindowState.Maximized;
        var width = maximized ? _lastNormalWidth : Width;
        var height = maximized ? _lastNormalHeight : Height;
        _viewModel.SaveWindowState(width, height, maximized);
        _viewModel.Services.Windows.SaveSession();
    }

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        if (WindowState == WindowState.Normal && _initialized)
        {
            _lastNormalWidth = Width;
            _lastNormalHeight = Height;
        }
        UpdateSidebarColumn();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsSidebarVisible) or nameof(MainViewModel.SidebarWidth))
            UpdateSidebarColumn();

        if (e.PropertyName == nameof(MainViewModel.IsCommandPaletteOpen) && _viewModel?.IsCommandPaletteOpen == true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                PaletteSearch.Focus();
                PaletteSearch.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private double MaximumSidebarWidth =>
        Bounds.Width <= 1 ? 480 : Math.Clamp(Bounds.Width - 520, 200, 480);

    private void UpdateSidebarColumn()
    {
        if (_viewModel is null)
            return;

        if (RootGrid.ColumnDefinitions.Count == 0)
            return;

        var max = MaximumSidebarWidth;
        var width = _viewModel.IsSidebarVisible
            ? Math.Clamp(_viewModel.SidebarWidth, 200, max)
            : 0;
        RootGrid.ColumnDefinitions[0].Width = new GridLength(width);
    }

    private void OnSidebarSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_viewModel is null)
            return;
        _viewModel.SidebarWidth = Math.Clamp(RootGrid.ColumnDefinitions[0].ActualWidth, 200, MaximumSidebarWidth);
        UpdateSidebarColumn();
    }

    private void OnTabScrollerWheel(object? sender, PointerWheelEventArgs e)
    {
        var max = Math.Max(0, TabScroller.Extent.Width - TabScroller.Viewport.Width);
        var offset = Math.Clamp(TabScroller.Offset.X - e.Delta.Y * 60, 0, max);
        TabScroller.Offset = new Vector(offset, 0);
        e.Handled = true;
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || sender is not Border { DataContext: TabViewModel tab } border)
            return;

        var point = e.GetCurrentPoint(border);
        if (point.Properties.IsMiddleButtonPressed)
        {
            _viewModel.CloseTab(tab);
            e.Handled = true;
            return;
        }

        _viewModel.ActivateTab(tab);

        if (point.Properties.IsRightButtonPressed)
        {
            var menu = new ContextMenu();
            menu.Items.Add(CreateMenuItem(L10n.T("复制标签页"), () =>
            {
                _viewModel.ActivateTab(tab);
                _viewModel.DuplicateActiveTabCommand.Execute(null);
            }));
            menu.Items.Add(CreateMenuItem(tab.IsPinned ? L10n.T("取消固定标签页") : L10n.T("固定标签页"),
                () => _viewModel.TogglePinTab(tab)));
            menu.Items.Add(CreateMenuItem(L10n.T("关闭标签页"), () => _viewModel.CloseTab(tab)));
            menu.Items.Add(CreateMenuItem(L10n.T("关闭其他标签页"), () => _viewModel.CloseOtherTabs(tab)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(L10n.T("恢复关闭的标签页"),
                () => _viewModel.ReopenClosedTabCommand.Execute(null)));
            menu.Items.Add(CreateMenuItem(L10n.T("新建标签页"), () => _viewModel.NewTabCommand.Execute(null)));
            border.ContextMenu = menu;
            menu.Open(border);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _dragTab = tab;
            _dragTabBorder = border;
            _tabDragTrigger = e;
            _tabDragStart = e.GetPosition(this);
            _tabDragging = false;
        }
    }

    private async void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_viewModel is null || _dragTab is null || _dragTabBorder is null)
            return;

        var point = e.GetPosition(this);
        if (!_tabDragging)
        {
            if (Math.Abs(point.X - _tabDragStart.X) < 6 && Math.Abs(point.Y - _tabDragStart.Y) < 6)
                return;
            _tabDragging = true;
            e.Pointer.Capture(_dragTabBorder);
        }

        if (point.X < 0 || point.Y < 0 || point.X > Bounds.Width || point.Y > Bounds.Height)
        {
            var tab = _dragTab;
            await BeginCrossWindowTabDragAsync(e, tab);
            return;
        }

        var hit = this.InputHitTest(point) as Visual;
        var target = hit?.GetSelfAndVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(candidate => candidate.DataContext is TabViewModel &&
                                        candidate.Classes.Contains("tab"));
        if (target?.DataContext is not TabViewModel targetTab || ReferenceEquals(targetTab, _dragTab))
            return;

        var targetIndex = _viewModel.Tabs.IndexOf(targetTab);
        if (targetIndex >= 0)
            _viewModel.MoveTab(_dragTab, targetIndex);
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndTabDrag(e.Pointer);
    }

    private void OnTabPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragTab = null;
        _dragTabBorder = null;
        _tabDragTrigger = null;
        _tabDragging = false;
    }

    private const string TabDragPrefix = "ordoa-tab:";

    private async Task BeginCrossWindowTabDragAsync(PointerEventArgs e, TabViewModel tab)
    {
        if (_viewModel is null)
            return;

        var trigger = _tabDragTrigger;
        EndTabDrag(e.Pointer);
        if (trigger is null)
            return;

        var token = _viewModel.Services.TabDrags.Register(_viewModel, tab);
        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(TabDragPrefix + token));
            await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Move);
        }
        catch
        {
            // A cancelled drag leaves the source tab untouched.
        }
        finally
        {
            _viewModel.Services.TabDrags.Cancel(token);
        }
    }

    private void OnTabStripDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private async void OnTabStripDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is null)
            return;

        var text = e.DataTransfer.TryGetText();
        if (string.IsNullOrEmpty(text) || !text.StartsWith(TabDragPrefix, StringComparison.Ordinal))
            return;

        var token = text[TabDragPrefix.Length..];
        if (!_viewModel.Services.TabDrags.TryTake(token, out var payload) || payload is null)
            return;

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        if (ReferenceEquals(payload.Source, _viewModel) ||
            !payload.Source.Tabs.Contains(payload.Tab))
            return;

        var state = payload.Source.SnapshotTab(payload.Tab);
        var adopted = await _viewModel.AdoptTabAsync(state);
        if (adopted is not null)
            payload.Source.CloseTabWithoutHistory(payload.Tab);
    }

    private void EndTabDrag(IPointer? pointer)
    {
        pointer?.Capture(null);
        _dragTab = null;
        _dragTabBorder = null;
        _tabDragTrigger = null;
        _tabDragging = false;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: TabViewModel tab })
            return;
        _viewModel.CloseTab(tab);
        e.Handled = true;
    }

    private void OnDismissToastClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Button { DataContext: ToastViewModel toast })
            _viewModel.DismissToast(toast);
    }

    private async void OnToolbarCommandClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { Tag: string id } && DataContext is MainViewModel vm)
            await vm.RunToolbarCommandAsync(id);
    }

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender)
            _viewModel?.CloseCommandPaletteCommand.Execute(null);
    }

    private void OnPaletteItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null)
            return;
        var command = (sender as Control)?.DataContext as PaletteCommand ?? PaletteList.SelectedItem as PaletteCommand;
        _ = _viewModel.ExecutePaletteCommandAsync(command);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;

        // Modal dialogs own the keyboard; never run main-window shortcuts
        // behind them (which used to open duplicate dialogs on key repeat).
        if (App.Services?.Dialogs.HasOpenDialogFor(this) == true)
            return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (_viewModel.IsCommandPaletteOpen)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _viewModel.CloseCommandPaletteCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Up:
                    _viewModel.MovePaletteSelection(-1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    _viewModel.MovePaletteSelection(1);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    _ = _viewModel.ExecuteSelectedPaletteCommandAsync();
                    e.Handled = true;
                    return;
            }
        }

        if (_viewModel.Preview.IsOpen)
        {
            if (ctrl && e.Key is Key.C or Key.A)
                return;

            switch (e.Key)
            {
                case Key.Escape:
                case Key.Space:
                    _viewModel.Preview.Close();
                    e.Handled = true;
                    return;
                case Key.Enter:
                    Run(_viewModel.OpenPreviewedAsync);
                    e.Handled = true;
                    return;
            }

            // The overlay owns the keyboard while it is visible.
            e.Handled = true;
            return;
        }

        var focused = FocusManager?.GetFocusedElement();
        var inTextBox = focused is TextBox;

        if (inTextBox)
            return;

        if (_viewModel.Services.Shortcuts.TryMatch(e, OperatingSystem.IsMacOS(), out var shortcut))
        {
            ExecuteShortcut(shortcut, _viewModel.ActivePane);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            var pane = _viewModel.ActivePane;
            if (pane is null)
                return;
            if (pane.IsEditingLocation)
                pane.CancelEditLocation();
            else if (pane.IsSearchActive)
                pane.ExitSearch();
            else
                pane.ClearSelection();
            e.Handled = true;
            return;
        }

        var active = _viewModel.ActivePane;

        if (ctrl && e.Key == Key.Tab)
        {
            SwitchTab(shift ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (alt)
        {
            switch (e.Key)
            {
                case Key.Home:
                    _viewModel.OpenHomeCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Left:
                    active?.GoBackCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Right:
                    active?.GoForwardCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Up:
                    active?.GoUpCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    _ = active?.PropertiesAsync();
                    e.Handled = true;
                    return;
            }

            if (e.Key is >= Key.D1 and <= Key.D9)
            {
                var index = (int)e.Key - (int)Key.D1;
                if (index < _viewModel.Tabs.Count)
                    _viewModel.ActivateTab(_viewModel.Tabs[index]);
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.F11:
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                e.Handled = true;
                break;
            case Key.Back:
                if (active is { CanGoUp: true })
                    active.GoUpCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void ExecuteShortcut(string id, PaneViewModel? pane)
    {
        if (_viewModel is null)
            return;

        switch (id)
        {
            case "newTab":
                _viewModel.NewTabCommand.Execute(null);
                break;
            case "newWindow":
                _viewModel.NewWindowCommand.Execute(null);
                break;
            case "closeTab":
                _viewModel.CloseActiveTabCommand.Execute(null);
                break;
            case "closeSplit":
                if (_viewModel.ActiveTab is { IsSplit: true })
                    _viewModel.ToggleSplitCommand.Execute(null);
                break;
            case "reopenTab":
                Run(_viewModel.ReopenClosedTabAsync);
                break;
            case "commandPalette":
                _viewModel.ShowCommandPaletteCommand.Execute(null);
                break;
            case "quickPreview":
                Run(_viewModel.TogglePreviewAsync);
                break;
            case "undo":
                Run(_viewModel.UndoAsync);
                break;
            case "redo":
                Run(_viewModel.RedoAsync);
                break;
            case "openWith":
                if (pane is { CanOpenWith: true })
                    Run(pane.OpenWithAsync);
                break;
            case "batchRename":
                if (pane is { CanBatchRename: true })
                    Run(pane.BatchRenameAsync);
                break;
            case "editLocation":
                pane?.BeginEditLocation();
                break;
            case "search":
                pane?.RequestSearchFocus();
                break;
            case "toggleHidden":
                _viewModel.ToggleHiddenCommand.Execute(null);
                break;
            case "bookmark":
                Run(_viewModel.AddCurrentBookmarkAsync);
                break;
            case "settings":
                Run(_viewModel.ShowSettingsAsync);
                break;
            case "toggleSplit":
                _viewModel.ToggleSplitCommand.Execute(null);
                break;
            case "listView":
                _viewModel.SetView(Ordoa.Core.Models.ViewKind.List);
                break;
            case "gridView":
                _viewModel.SetView(Ordoa.Core.Models.ViewKind.Grid);
                break;
            case "newFolder":
                if (pane is { CanCreateFolder: true })
                    Run(pane.NewFolderAsync);
                break;
            case "about":
                Run(() => _viewModel.Services.Dialogs.ShowAboutAsync());
                break;
            case "rename":
                if (pane is { CanRename: true })
                    Run(pane.RenameAsync);
                break;
            case "previewPanel":
                _viewModel.TogglePreviewPanelCommand.Execute(null);
                break;
            case "refresh":
                if (pane is not null)
                    Run(pane.RefreshAsync);
                break;
            case "toggleSidebar":
                _viewModel.ToggleSidebarCommand.Execute(null);
                break;
            case "delete":
                if (pane is { HasSelection: true })
                    Run(() => pane.DeleteSelectionAsync(false));
                break;
            case "permanentDelete":
                if (pane is { HasSelection: true })
                    Run(() => pane.DeleteSelectionAsync(true));
                break;
            case "open":
                if (pane is not null)
                    Run(() => pane.OpenAsync());
                break;
            case "copy":
                if (pane is { HasSelection: true })
                    Run(() => pane.CopySelectionAsync(false));
                break;
            case "cut":
                if (pane is { HasSelection: true })
                    Run(() => pane.CopySelectionAsync(true));
                break;
            case "paste":
                if (pane is { CanPaste: true })
                    Run(pane.PasteAsync);
                break;
            case "selectAll":
                pane?.RequestSelectAll();
                break;
        }
    }

    private void SwitchTab(int delta)
    {
        if (_viewModel is null || _viewModel.Tabs.Count == 0 || _viewModel.ActiveTab is null)
            return;
        var index = _viewModel.Tabs.IndexOf(_viewModel.ActiveTab);
        index = (index + delta + _viewModel.Tabs.Count) % _viewModel.Tabs.Count;
        _viewModel.ActivateTab(_viewModel.Tabs[index]);
    }

    private async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error("快捷键操作失败", ex);
            _viewModel?.AddToast(L10n.T("操作失败"), ex.Message, ToastKind.Error);
        }
    }
}
