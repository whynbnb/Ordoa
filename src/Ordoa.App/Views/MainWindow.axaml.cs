using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
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

    public MainWindow()
    {
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
        _viewModel.SaveSession();
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
            menu.Items.Add(CreateMenuItem(L10n.T("关闭标签页"), () => _viewModel.CloseTab(tab)));
            menu.Items.Add(CreateMenuItem(L10n.T("关闭其他标签页"), () => _viewModel.CloseOtherTabs(tab)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(L10n.T("新建标签页"), () => _viewModel.NewTabCommand.Execute(null)));
            border.ContextMenu = menu;
            menu.Open(border);
            e.Handled = true;
        }
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

        if (e.Key == Key.Space && !ctrl && !shift && !alt)
        {
            Run(_viewModel.TogglePreviewAsync);
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

        if (ctrl && shift && e.Key == Key.P)
        {
            _viewModel.ShowCommandPaletteCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.T:
                    _viewModel.NewTabCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.W:
                    if (shift && _viewModel.ActiveTab is { IsSplit: true })
                        _viewModel.ToggleSplitCommand.Execute(null);
                    else
                        _viewModel.CloseActiveTabCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Tab:
                    SwitchTab(shift ? -1 : 1);
                    e.Handled = true;
                    return;
                case Key.L:
                    active?.BeginEditLocation();
                    e.Handled = true;
                    return;
                case Key.F:
                    active?.RequestSearchFocus();
                    e.Handled = true;
                    return;
                case Key.H:
                    _viewModel.ToggleHiddenCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.D:
                    Run(_viewModel.AddCurrentBookmarkAsync);
                    e.Handled = true;
                    return;
                case Key.OemComma:
                    Run(_viewModel.ShowSettingsAsync);
                    e.Handled = true;
                    return;
                case Key.OemBackslash:
                case Key.OemPipe:
                    _viewModel.ToggleSplitCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.D1:
                    _viewModel.SetView(Ordoa.Core.Models.ViewKind.List);
                    e.Handled = true;
                    return;
                case Key.D2:
                    _viewModel.SetView(Ordoa.Core.Models.ViewKind.Grid);
                    e.Handled = true;
                    return;
                case Key.C:
                    if (active is { HasSelection: true })
                        Run(() => active.CopySelectionAsync(false));
                    e.Handled = true;
                    return;
                case Key.X:
                    if (active is { HasSelection: true })
                        Run(() => active.CopySelectionAsync(true));
                    e.Handled = true;
                    return;
                case Key.V:
                    if (active is { CanPaste: true })
                        Run(active.PasteAsync);
                    e.Handled = true;
                    return;
                case Key.A:
                    active?.RequestSelectAll();
                    e.Handled = true;
                    return;
            }
        }

        if (shift && ctrl && e.Key == Key.N)
        {
            if (active is { CanCreateFolder: true })
                Run(active.NewFolderAsync);
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
            case Key.F1:
                Run(() => _viewModel.Services.Dialogs.ShowAboutAsync());
                e.Handled = true;
                break;
            case Key.F2:
                if (active is { CanRename: true })
                    Run(active.RenameAsync);
                e.Handled = true;
                break;
            case Key.F5:
                if (active is not null)
                    Run(active.RefreshAsync);
                e.Handled = true;
                break;
            case Key.F9:
                _viewModel.ToggleSidebarCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F11:
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                e.Handled = true;
                break;
            case Key.Delete:
                if (active is { HasSelection: true })
                    Run(() => active.DeleteSelectionAsync(shift));
                e.Handled = true;
                break;
            case Key.Enter:
                if (active is not null)
                    Run(() => active.OpenAsync());
                e.Handled = true;
                break;
            case Key.Back:
                if (active is { CanGoUp: true })
                    active.GoUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
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
