using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ordoa.App.Services;
using Ordoa.Core.Abstractions;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;
using Ordoa.Core.Localization;

namespace Ordoa.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly HashSet<PaneViewModel> _subscribedPanes = [];
    private readonly bool _restoreSession;
    private readonly string? _startupLocation;
    private readonly IReadOnlyList<string>? _startupSelection;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<VolumeInfo> _volumes = [];
    private readonly List<ClosedTabState> _closedTabs = [];
    private const int MaxClosedTabs = 20;
    private bool _initialized;
    private bool _disposed;
    private bool _refreshingSidebar;
    private bool _settingsDialogOpen;

    public MainViewModel(AppServices services, bool restoreSession = true,
        string? startupLocation = null, IReadOnlyList<string>? startupSelection = null)
    {
        _services = services;
        _restoreSession = restoreSession;
        _startupLocation = startupLocation;
        _startupSelection = startupSelection;
        Preview = new FilePreviewViewModel(services);
        PreviewPanel = new FilePreviewViewModel(services);
        _isPreviewPanelOpen = services.Settings.Current.ShowPreviewPanel;
        _sidebarVisible = services.Settings.Current.ShowSidebar;
        _sidebarWidth = services.Settings.Current.SidebarWidth;
        _isTransferPanelOpen = false;

        services.Settings.Changed += OnSettingsChanged;
        L10n.LanguageChanged += OnLanguageChanged;
        services.Operations.Finished += OnOperationFinished;
        services.Operations.Operations.CollectionChanged += OnOperationsChanged;
        services.FileSystems.Connections.Changed += OnConnectionsChanged;

        BuildPaletteCommands();
        BuildSidebar();
    }

    public AppServices Services => _services;
    public FilePreviewViewModel Preview { get; }
    public FilePreviewViewModel PreviewPanel { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    public ObservableCollection<SidebarSectionViewModel> SidebarSections { get; } = [];

    private SidebarSectionViewModel? _systemSection;
    /// <summary>Pinned above the sidebar footer so it never scrolls away.</summary>
    public SidebarSectionViewModel? SystemSection
    {
        get => _systemSection;
        private set => SetProperty(ref _systemSection, value);
    }

    public IReadOnlyList<Ordoa.Core.Models.CustomCommand> ToolbarCommands
        => _services.CustomCommands.GetToolbarCommands();
    public bool HasToolbarCommands => ToolbarCommands.Count > 0;

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];
    public ObservableCollection<PaletteCommand> PaletteCommands { get; } = [];
    public ObservableCollection<PaletteCommand> FilteredCommands { get; } = [];

    private TabViewModel? _activeTab;

    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (ReferenceEquals(_activeTab, value))
                return;
            if (_activeTab is not null)
                _activeTab.IsActive = false;
            if (!SetProperty(ref _activeTab, value))
                return;

            if (Preview.IsOpen)
                Preview.Close();
            if (value is not null)
                value.IsActive = true;

            OnPropertyChanged(nameof(ActivePane));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(HasTabs));
            if (IsPreviewPanelOpen)
                _ = RefreshPreviewPanelAsync();
            ActiveTabChanged?.Invoke(value);
            OnPropertyChanged(nameof(CanCloseActiveTab));
            UpdateWindowTitle();
            UpdateSidebarActive();
        }
    }

    public PaneViewModel? ActivePane => ActiveTab?.ActivePane;
    public bool HasTabs => Tabs.Count > 0;
    public bool CanCloseActiveTab => Tabs.Count > 0;

    private bool _isPreviewPanelOpen;

    public bool IsPreviewPanelOpen
    {
        get => _isPreviewPanelOpen;
        set
        {
            if (!SetProperty(ref _isPreviewPanelOpen, value))
                return;

            _services.Settings.Current.ShowPreviewPanel = value;
            _services.Settings.Save();
            NotifyPreviewPanelLayoutChanged();

            if (value)
                _ = RefreshPreviewPanelAsync();
            else
                PreviewPanel.Close();
        }
    }

    public bool IsPreviewPanelAtRight =>
        _services.Settings.Current.PreviewPanelPlacement == PreviewPanelPlacement.Right;
    public bool IsPreviewPanelAtBottom => !IsPreviewPanelAtRight;
    public bool ShowPreviewPanelRight => IsPreviewPanelOpen && IsPreviewPanelAtRight;
    public bool ShowPreviewPanelBottom => IsPreviewPanelOpen && IsPreviewPanelAtBottom;

    private void NotifyPreviewPanelLayoutChanged()
    {
        OnPropertyChanged(nameof(IsPreviewPanelAtRight));
        OnPropertyChanged(nameof(IsPreviewPanelAtBottom));
        OnPropertyChanged(nameof(ShowPreviewPanelRight));
        OnPropertyChanged(nameof(ShowPreviewPanelBottom));
    }

    private bool _sidebarVisible;
    public bool IsSidebarVisible
    {
        get => _sidebarVisible;
        set
        {
            if (!SetProperty(ref _sidebarVisible, value))
                return;
            _services.Settings.Current.ShowSidebar = value;
            _services.Settings.Save();
            OnPropertyChanged(nameof(ReserveMacOSTrafficLightsInTitleBar));
        }
    }

    private double _sidebarWidth;
    public double SidebarWidth
    {
        get => _sidebarWidth;
        set
        {
            if (SetProperty(ref _sidebarWidth, Math.Clamp(value, 200, 480)))
                _services.Settings.Current.SidebarWidth = _sidebarWidth;
        }
    }

    private string _windowTitle = "Ordoa";
    public string WindowTitle
    {
        get => _windowTitle;
        private set => SetProperty(ref _windowTitle, value);
    }

    public bool UseSystemWindowFrame => _services.Settings.Current.UseSystemWindowFrame;

    public bool IsImmersiveWindow => !UseSystemWindowFrame;

    public bool IsMacOS => OperatingSystem.IsMacOS();

    /// <summary>
    /// With the immersive title bar macOS already shows the app name and icon
    /// in the menu bar and Dock. Repeating the product lock-up inside the
    /// sidebar makes the window look like a Windows app, so the title-bar strip
    /// stays quiet and the sidebar starts with its first section. The native
    /// window frame has no traffic lights over the sidebar, so the branding is
    /// restored there to avoid an empty band above the sidebar.
    /// </summary>
    public bool ShowSidebarBranding => !IsMacOS || !IsImmersiveWindow;

    public bool ShowWindowCaptionButtons => IsImmersiveWindow && !IsMacOS &&
        (_services.Settings.Current.WindowButtonMode switch
        {
            WindowButtonMode.Show => true,
            WindowButtonMode.Hide => false,
            _ => !WindowEnvironment.IsTilingCompositor,
        });

    public bool ReserveMacOSTrafficLights => IsImmersiveWindow && IsMacOS;

    /// <summary>Only the title bar at the far left needs the traffic-light gap.</summary>
    public bool ReserveMacOSTrafficLightsInTitleBar
        => ReserveMacOSTrafficLights && !IsSidebarVisible;

    private bool _isInitialized;
    public bool IsInitialized
    {
        get => _isInitialized;
        private set => SetProperty(ref _isInitialized, value);
    }

    private string _startupStatus = L10n.T("正在启动…");
    public string StartupStatus
    {
        get => _startupStatus;
        private set => SetProperty(ref _startupStatus, value);
    }

    private bool _isCommandPaletteOpen;
    public bool IsCommandPaletteOpen
    {
        get => _isCommandPaletteOpen;
        set
        {
            if (SetProperty(ref _isCommandPaletteOpen, value) && value)
                CommandPaletteOpened?.Invoke();
        }
    }

    private string _paletteQuery = string.Empty;
    public string PaletteQuery
    {
        get => _paletteQuery;
        set
        {
            if (!SetProperty(ref _paletteQuery, value))
                return;
            FilterPalette();
        }
    }

    private PaletteCommand? _selectedPaletteCommand;
    public PaletteCommand? SelectedPaletteCommand
    {
        get => _selectedPaletteCommand;
        set => SetProperty(ref _selectedPaletteCommand, value);
    }

    private bool _isTransferPanelOpen;
    public bool IsTransferPanelOpen
    {
        get => _isTransferPanelOpen;
        set
        {
            SetProperty(ref _isTransferPanelOpen, value);
        }
    }


    private bool _hasTransfers;
    public bool HasTransfers
    {
        get => _hasTransfers;
        private set => SetProperty(ref _hasTransfers, value);
    }

    private int _activeTransferCount;
    public int ActiveTransferCount
    {
        get => _activeTransferCount;
        private set => SetProperty(ref _activeTransferCount, value);
    }

    public string TransferSummary
    {
        get
        {
            if (ActiveTransferCount > 0)
                return L10n.Format("{0} 个进行中", ActiveTransferCount);
            if (!HasTransfers)
                return L10n.T("没有传输任务");
            var finished = _services.Operations.Operations.Count(
                o => o.IsCompleted || o.IsFailed || o.IsCancelled);
            return finished > 0 ? L10n.Format("{0} 个已完成", finished) : L10n.T("传输队列");
        }
    }

    public event Action? CloseWindowRequested;
    public event Action? CommandPaletteOpened;
    public event Action<TabViewModel?>? ActiveTabChanged;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            _ready.TrySetResult(true);
            return;
        }

        IsInitialized = false;
        StartupStatus = L10n.T("正在准备…");

        var startupLocation = _startupLocation ?? (_restoreSession ? GetStartupLocation() : null);
        var restored = false;

        if (_restoreSession && _services.Settings.Current.RestoreSession)
        {
            StartupStatus = L10n.T("正在恢复上次会话…");
            restored = await RestoreSessionAsync().ConfigureAwait(true);
        }

        if (!restored)
        {
            if (startupLocation is not null)
            {
                try
                {
                    await CreateTabForLocationAsync(startupLocation, true, _startupSelection).ConfigureAwait(true);
                }
                catch
                {
                    await CreateTabAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath, true).ConfigureAwait(true);
                }
            }
            else
            {
                await CreateTabAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath, true).ConfigureAwait(true);
            }
        }
        else if (startupLocation is not null)
        {
            try
            {
                await CreateTabForLocationAsync(startupLocation, true, _startupSelection).ConfigureAwait(true);
            }
            catch { /* ignore invalid startup path */ }
        }

        if (Tabs.Count == 0)
            await CreateTabAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath, true).ConfigureAwait(true);

        _initialized = true;
        IsInitialized = true;
        StartupStatus = string.Empty;
        OnPropertyChanged(nameof(HasTabs));
        await RefreshSidebarAsync().ConfigureAwait(true);
        _ready.TrySetResult(true);
        if (IsPreviewPanelOpen)
            _ = RefreshPreviewPanelAsync();
    }

    public void EnsureInitialized()
    {
        _initialized = true;
        IsInitialized = true;
        StartupStatus = string.Empty;
        if (Tabs.Count == 0)
            _ = CreateTabAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath, true);
        _ready.TrySetResult(true);
    }

    public Task WaitForReadyAsync() => _ready.Task;

    public async Task<TabViewModel> OpenLocationAsync(string location, IReadOnlyList<string>? selection = null)
    {
        await WaitForReadyAsync().ConfigureAwait(true);
        var tab = await CreateTabForLocationAsync(location, true, selection).ConfigureAwait(true);
        UpdateWindowTitle();
        UpdateSidebarActive();
        return tab;
    }

    public async Task<TabViewModel> CreateTabAsync(IFileSystem fileSystem, string path, bool activate = true)
    {
        var pane = new PaneViewModel(_services, fileSystem, path);
        var tab = new TabViewModel(_services, pane);
        SubscribeTab(tab);
        SubscribePane(pane);
        Tabs.Add(tab);
        if (activate || ActiveTab is null)
            ActiveTab = tab;
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(CanCloseActiveTab));
        await pane.InitializeAsync().ConfigureAwait(true);
        UpdateWindowTitle();
        UpdateSidebarActive();
        return tab;
    }

    public Task UndoAsync() => _services.Undo.UndoAsync();

    public Task RedoAsync() => _services.Undo.RedoAsync();

    public async Task<TabViewModel> DuplicateTabAsync()
    {
        var pane = ActivePane;
        if (pane is null)
            return await CreateTabAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath).ConfigureAwait(true);
        return await CreateTabAsync(pane.FileSystem, pane.CurrentPath).ConfigureAwait(true);
    }

    public void ActivateTab(TabViewModel tab)
    {
        if (Tabs.Contains(tab))
            ActiveTab = tab;
    }

    [RelayCommand]
    private async Task NewTabAsync()
        => await CreateTabAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath, true).ConfigureAwait(true);

    [RelayCommand]
    private async Task NewWindowAsync()
    {
        if (!_services.Settings.Current.AllowMultipleWindows)
        {
            AddToast(L10n.T("无法新建窗口"), L10n.T("设置中已关闭多窗口模式"), ToastKind.Info);
            return;
        }

        var pane = ActivePane;
        var location = pane is { FileSystem.IsLocal: true, IsTrash: false } ? pane.CurrentPath : null;
        await _services.Windows.CreateWindowAsync(location, restoreSession: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DuplicateActiveTabAsync()
        => await DuplicateTabAsync().ConfigureAwait(true);

    public int PinnedTabCount => Tabs.Count(tab => tab.IsPinned);

    public bool CanReopenClosedTab => _closedTabs.Count > 0;

    [RelayCommand]
    public async Task ReopenClosedTabAsync()
    {
        if (_closedTabs.Count == 0)
        {
            AddToast(L10n.T("没有可恢复的标签页"), L10n.T("关闭标签页后可以用 Ctrl+Shift+T 恢复"), ToastKind.Info);
            return;
        }

        var closed = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        var tab = await CreateTabFromSessionAsync(closed.Tab, closed.Index, closed.FileSystems).ConfigureAwait(true);
        if (tab is null)
            return;

        tab.IsPinned = closed.Tab.IsPinned;
        if (closed.Tab.IsPinned)
            MoveTab(tab, Math.Min(closed.Index, PinnedTabCount - 1));
        ActivateTab(tab);
    }

    [RelayCommand]
    public void TogglePinTab(TabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null)
            return;

        if (tab.IsPinned)
        {
            tab.IsPinned = false;
            var index = Tabs.IndexOf(tab);
            var pinned = PinnedTabCount;
            if (index < pinned)
                MoveTab(tab, pinned);
        }
        else
        {
            tab.IsPinned = true;
            MoveTab(tab, PinnedTabCount - 1);
        }

        OnPropertyChanged(nameof(PinnedTabCount));
        UpdateWindowTitle();
    }

    /// <summary>Moves a tab while keeping pinned tabs grouped before regular tabs.</summary>
    public void MoveTab(TabViewModel tab, int targetIndex)
    {
        var current = Tabs.IndexOf(tab);
        if (current < 0)
            return;

        var pinned = PinnedTabCount;
        var min = tab.IsPinned ? 0 : pinned;
        var max = tab.IsPinned ? Math.Max(0, pinned - 1) : Tabs.Count - 1;
        targetIndex = Math.Clamp(targetIndex, min, max);
        if (targetIndex == current)
            return;

        Tabs.Move(current, targetIndex);
        OnPropertyChanged(nameof(PinnedTabCount));
    }

    [RelayCommand]
    public void CloseTab(TabViewModel? tab) => CloseTabCore(tab, remember: true);

    /// <summary>Removes a tab without adding it to the reopen history.</summary>
    public void CloseTabWithoutHistory(TabViewModel tab) => CloseTabCore(tab, remember: false);

    private void CloseTabCore(TabViewModel? tab, bool remember)
    {
        tab ??= ActiveTab;
        if (tab is null)
            return;

        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;

        if (remember && Tabs.Count > 1 && !_disposed)
            RememberClosedTab(tab, index);

        foreach (var pane in tab.Panes.ToList())
        {
            UnsubscribePane(pane);
        }
        tab.Dispose();
        Tabs.RemoveAt(index);

        if (Tabs.Count == 0)
        {
            ActiveTab = null;
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(CanCloseActiveTab));
            CloseWindowRequested?.Invoke();
            return;
        }

        if (ReferenceEquals(ActiveTab, tab))
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];

        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(CanCloseActiveTab));
        UpdateWindowTitle();
        UpdateSidebarActive();
    }

    [RelayCommand]
    private void CloseActiveTab() => CloseTab(ActiveTab);

    [RelayCommand]
    public void CloseOtherTabs(TabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null)
            return;
        foreach (var other in Tabs.Where(t => !ReferenceEquals(t, tab)).ToList())
            CloseTab(other);
        ActiveTab = tab;
    }

    [RelayCommand]
    private async Task ToggleSplitAsync()
    {
        if (ActiveTab is null)
            return;
        var before = ActiveTab.Panes.ToList();
        await ActiveTab.ToggleSplitAsync().ConfigureAwait(true);
        foreach (var pane in ActiveTab.Panes)
        {
            if (!before.Contains(pane))
                SubscribePane(pane);
        }
        foreach (var pane in before.Where(p => !ActiveTab.Panes.Contains(p)))
            UnsubscribePane(pane);
        OnPropertyChanged(nameof(ActivePane));
        UpdateWindowTitle();
        UpdateSidebarActive();
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private void ToggleTransferPanel() => IsTransferPanelOpen = !IsTransferPanelOpen;

    [RelayCommand]
    private void TogglePreviewPanel() => IsPreviewPanelOpen = !IsPreviewPanelOpen;

    public async Task RefreshPreviewPanelAsync()
    {
        if (!IsPreviewPanelOpen)
            return;

        var pane = ActivePane;
        if (pane?.PrimarySelection is not { } item)
        {
            PreviewPanel.Close();
            return;
        }

        await PreviewPanel.OpenAsync(pane, item).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenPreviewPanelItemAsync()
    {
        if (PreviewPanel is not { Pane: { } pane, Item: { } item })
            return;
        await pane.OpenAsync(item).ConfigureAwait(true);
    }

    public async Task TogglePreviewAsync()
    {
        if (Preview.IsOpen)
        {
            Preview.Close();
            return;
        }

        var pane = ActivePane;
        if (pane?.PrimarySelection is not { } item)
            return;
        await Preview.OpenAsync(pane, item).ConfigureAwait(true);
    }

    public async Task OpenPreviewedAsync()
    {
        if (Preview is not { Pane: { } pane, Item: { } item })
            return;
        Preview.Close();
        await pane.OpenAsync(item).ConfigureAwait(true);
    }

    private async void OnPanePreviewRequested(PaneViewModel pane)
    {
        if (pane.PrimarySelection is not { } item)
            return;
        await Preview.OpenAsync(pane, item).ConfigureAwait(true);
    }

    private async void OnPaneSelectionChanged(PaneViewModel pane)
    {
        if (ReferenceEquals(pane, ActivePane) && IsPreviewPanelOpen)
            await RefreshPreviewPanelAsync().ConfigureAwait(true);

        if (!Preview.IsOpen || !ReferenceEquals(pane, ActivePane))
            return;
        if (pane.PrimarySelection is not { } item)
        {
            Preview.Close();
            return;
        }
        await Preview.OpenAsync(pane, item).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAllAsync() => await RefreshAllPanesAsync().ConfigureAwait(true);

    [RelayCommand]
    private void ToggleHidden()
    {
        _services.Settings.Current.ShowHidden = !_services.Settings.Current.ShowHidden;
        _services.Settings.Save();
    }

    [RelayCommand]
    private void SetListView() => SetView(ViewKind.List);

    [RelayCommand]
    private void SetGridView() => SetView(ViewKind.Grid);

    public void SetView(ViewKind view)
    {
        if (ActivePane is null)
            return;
        ActivePane.ViewMode = view;
    }

    [RelayCommand]
    private async Task OpenHomeAsync()
        => await NavigateActiveAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath).ConfigureAwait(true);

    [RelayCommand]
    private async Task OpenTrashAsync()
        => await NavigateActiveAsync(_services.TrashFs, "/").ConfigureAwait(true);

    [RelayCommand]
    private async Task AddBookmarkAsync() => await AddCurrentBookmarkAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task NewConnectionAsync() => await ShowConnectionDialogAsync(null).ConfigureAwait(true);

    [RelayCommand]
    private async Task OpenSettingsAsync() => await ShowSettingsAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task OpenAboutAsync() => await _services.Dialogs.ShowAboutAsync().ConfigureAwait(true);

    [RelayCommand]
    private void ShowCommandPalette()
    {
        PaletteQuery = string.Empty;
        FilterPalette();
        IsCommandPaletteOpen = true;
    }

    [RelayCommand]
    private void CloseCommandPalette() => IsCommandPaletteOpen = false;

    public async Task ExecutePaletteCommandAsync(PaletteCommand? command)
    {
        command ??= SelectedPaletteCommand ?? FilteredCommands.FirstOrDefault();
        if (command is null)
            return;
        IsCommandPaletteOpen = false;
        if (command.ExecuteAsync is not null)
        {
            try
            {
                await command.ExecuteAsync().ConfigureAwait(true);
                RecordPaletteUse(command.Title);
            }
            catch (Exception ex)
            {
                AddToast(L10n.T("操作失败"), ex.Message, ToastKind.Error);
            }
        }
    }

    private void RecordPaletteUse(string title)
    {
        var settings = _services.Settings.Current;
        settings.PaletteRecent.RemoveAll(entry => string.Equals(entry, title, StringComparison.Ordinal));
        settings.PaletteRecent.Insert(0, title);
        while (settings.PaletteRecent.Count > 16)
            settings.PaletteRecent.RemoveAt(settings.PaletteRecent.Count - 1);
        settings.PaletteUsage[title] = settings.PaletteUsage.GetValueOrDefault(title) + 1;
        _services.Settings.Save();
    }

    public async Task RunToolbarCommandAsync(string id)
    {
        var command = _services.CustomCommands.Find(id);
        if (command is null)
            return;
        var paths = ActivePane?.SelectedPaths ?? [];
        if (command.RequiresSelection && paths.Count == 0)
        {
            AddToast(L10n.T("无法运行命令"), L10n.T("该命令需要先选中文件或文件夹"), ToastKind.Error);
            return;
        }
        if (!await _services.CustomCommands.RunAsync(command, ActivePane?.CurrentPath ?? AppPaths.Home, paths)
                .ConfigureAwait(true))
            AddToast(L10n.T("无法运行命令"), command.Name, ToastKind.Error);
    }

    [RelayCommand]
    public async Task ExecuteSelectedPaletteCommandAsync()
        => await ExecutePaletteCommandAsync(SelectedPaletteCommand).ConfigureAwait(true);

    public void MovePaletteSelection(int delta)
    {
        if (FilteredCommands.Count == 0)
            return;
        var index = SelectedPaletteCommand is null ? -1 : FilteredCommands.IndexOf(SelectedPaletteCommand);
        index = Math.Clamp(index + delta, 0, FilteredCommands.Count - 1);
        SelectedPaletteCommand = FilteredCommands[index];
    }

    public async Task ShowConnectionDialogAsync(ConnectionProfile? preset)
    {
        ConnectionDialogResult? result;
        try
        {
            result = await _services.Dialogs.ShowConnectionAsync(preset).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddToast(L10n.T("连接失败"), ex.Message, ToastKind.Error);
            return;
        }

        if (result is null)
            return;

        var profile = result.Profile;
        IFileSystem fs;
        try
        {
            fs = await _services.FileSystems.ConnectAsync(profile, result.Password).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (result.SaveProfile)
            {
                // Keep failed connections in the sidebar so the user can edit
                // the configuration and retry instead of re-entering it.
                profile.LastError = ex.Message;
                _services.FileSystems.Connections.Save(profile);
            }
            AddToast(L10n.T("连接失败"), ex.Message, ToastKind.Error);
            return;
        }

        profile.LastError = null;
        if (result.SaveProfile)
            await _services.FileSystems.SaveProfileAsync(profile, result.Password,
                deletePassword: !profile.SavePassword).ConfigureAwait(true);

        try
        {
            await CreateTabAsync(fs, profile.NormalizeInitialPath(profile.InitialPath), true).ConfigureAwait(true);
            await RefreshSidebarAsync().ConfigureAwait(true);
            AddToast(L10n.T("已连接"), $"{profile.ProtocolLabel} · {profile.DisplayTarget}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            AddToast(L10n.T("无法打开连接"), ex.Message, ToastKind.Error);
        }
    }

    public async Task AddCurrentBookmarkAsync()
    {
        var pane = ActivePane;
        if (pane is null || pane.IsTrash)
        {
            AddToast(L10n.T("无法添加书签"), L10n.T("请先在文件夹中打开一个位置"), ToastKind.Info);
            return;
        }

        if (_services.Bookmarks.Contains(pane.FileSystem.Id, pane.CurrentPath))
        {
            AddToast(L10n.T("书签已存在"), pane.Title, ToastKind.Info);
            return;
        }

        _services.Bookmarks.Add(pane.FileSystem, pane.CurrentPath, pane.Title);
        RefreshSidebar();
        AddToast(L10n.T("已添加书签"), pane.Title, ToastKind.Success);
        await Task.CompletedTask;
    }

    public async Task RemoveBookmarkAsync(SidebarEntryViewModel entry)
    {
        if (entry.BookmarkId is null)
            return;
        _services.Bookmarks.Remove(entry.BookmarkId);
        RefreshSidebar();
        await Task.CompletedTask;
    }

    public async Task EditConnectionAsync(SidebarEntryViewModel entry)
    {
        if (entry.ConnectionId is null)
            return;
        var profile = _services.FileSystems.Connections.Get(entry.ConnectionId);
        if (profile is null)
            return;

        try
        {
            var result = await _services.Dialogs.ShowConnectionAsync(profile).ConfigureAwait(true);
            if (result is null)
                return;

            var updated = result.Profile;
            updated.LastError = null;
            var oldId = profile.Id;
            await _services.FileSystems.DisconnectAsync(oldId).ConfigureAwait(true);
            await _services.FileSystems.SaveProfileAsync(updated, result.Password,
                deletePassword: !updated.SavePassword).ConfigureAwait(true);

            var affected = Tabs.SelectMany(t => t.Panes).Where(p => p.FileSystem.Id == oldId).ToList();
            if (affected.Count > 0)
            {
                try
                {
                    var newFs = await _services.FileSystems.ConnectAsync(updated, result.Password).ConfigureAwait(true);
                    foreach (var pane in affected)
                    {
                        try
                        {
                            await pane.NavigateAsync(newFs, pane.CurrentPath).ConfigureAwait(true);
                        }
                        catch
                        {
                            await pane.NavigateAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath).ConfigureAwait(true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    updated.LastError = ex.Message;
                    _services.FileSystems.Connections.Save(updated);
                    AddToast(L10n.T("更新连接失败"), ex.Message, ToastKind.Error);
                    return;
                }
            }

            RefreshSidebar();
            AddToast(L10n.T("连接已更新"), updated.Name, ToastKind.Success);
        }
        catch (Exception ex)
        {
            AddToast(L10n.T("更新连接失败"), ex.Message, ToastKind.Error);
        }
    }

    public async Task RemoveConnectionAsync(SidebarEntryViewModel entry)
    {
        if (entry.ConnectionId is null)
            return;
        var ok = await _services.Dialogs.ConfirmAsync(L10n.T("移除远程连接？"),
            L10n.T("保存的连接和密码将被移除，当前打开的远程位置会返回主目录。"), L10n.T("移除"), true).ConfigureAwait(true);
        if (!ok)
            return;

        await _services.FileSystems.RemoveProfileAsync(entry.ConnectionId).ConfigureAwait(true);
        await HandleConnectionRemovedAsync(entry.ConnectionId).ConfigureAwait(true);
        AddToast(L10n.T("已移除连接"), entry.Label, ToastKind.Success);
    }

    /// <summary>Updates open panes after a connection was removed from settings.</summary>
    public async Task HandleConnectionRemovedAsync(string connectionId)
    {
        foreach (var pane in Tabs.SelectMany(t => t.Panes).ToList())
        {
            if (pane.FileSystem.Id == connectionId)
            {
                try
                {
                    await pane.NavigateAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath).ConfigureAwait(true);
                }
                catch { /* ignore */ }
            }
        }

        RefreshSidebar();
    }

    public async Task ActivateSidebarEntryAsync(SidebarEntryViewModel entry)
    {
        if (entry.Kind == SidebarEntryKind.Volume && !entry.VolumeMounted)
        {
            await MountVolumeAsync(entry).ConfigureAwait(true);
            return;
        }

        try
        {
            switch (entry.Kind)
            {
                case SidebarEntryKind.Place:
                case SidebarEntryKind.Volume:
                case SidebarEntryKind.Bookmark:
                case SidebarEntryKind.Recent:
                {
                    var fs = await ResolveSidebarFileSystemAsync(entry).ConfigureAwait(true);
                    if (fs is null)
                        return;
                    await NavigateActiveAsync(fs, entry.Path ?? fs.RootPath).ConfigureAwait(true);
                    break;
                }
                case SidebarEntryKind.Connection:
                {
                    if (entry.ConnectionId is null)
                        return;
                    var fs = await ResolveSidebarFileSystemAsync(entry).ConfigureAwait(true);
                    if (fs is null)
                        return;
                    await NavigateActiveAsync(fs, entry.Path ?? fs.RootPath).ConfigureAwait(true);
                    break;
                }
                case SidebarEntryKind.Trash:
                    await NavigateActiveAsync(_services.TrashFs, "/").ConfigureAwait(true);
                    break;
                case SidebarEntryKind.AddConnection:
                    await ShowConnectionDialogAsync(null).ConfigureAwait(true);
                    break;
                case SidebarEntryKind.AddBookmark:
                    await AddCurrentBookmarkAsync().ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            AddToast(L10n.T("无法打开位置"), ex.Message, ToastKind.Error);
        }
    }

    public async Task OpenSidebarEntryInNewTabAsync(SidebarEntryViewModel entry)
    {
        if (entry.Kind == SidebarEntryKind.AddConnection)
        {
            await ShowConnectionDialogAsync(null).ConfigureAwait(true);
            return;
        }
        if (entry.Kind == SidebarEntryKind.AddBookmark)
        {
            await AddCurrentBookmarkAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            var fs = await ResolveSidebarFileSystemAsync(entry).ConfigureAwait(true);
            if (fs is null)
                return;
            await CreateTabAsync(fs, entry.Path ?? fs.RootPath, true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddToast(L10n.T("无法打开位置"), ex.Message, ToastKind.Error);
        }
    }

    public async Task OpenSidebarEntryInSplitAsync(SidebarEntryViewModel entry)
    {
        if (ActiveTab is null)
        {
            await OpenSidebarEntryInNewTabAsync(entry).ConfigureAwait(true);
            return;
        }

        try
        {
            var fs = await ResolveSidebarFileSystemAsync(entry).ConfigureAwait(true);
            if (fs is null)
                return;
            var path = entry.Path ?? fs.RootPath;
            if (ActiveTab.IsSplit && ActiveTab.SecondaryPane is not null)
            {
                ActiveTab.SecondaryPane.Activate();
                await ActiveTab.SecondaryPane.NavigateAsync(fs, path).ConfigureAwait(true);
            }
            else
            {
                var before = ActiveTab.Panes.ToList();
                await ActiveTab.AddSecondaryPaneAsync(fs, path, true).ConfigureAwait(true);
                foreach (var pane in ActiveTab.Panes.Where(p => !before.Contains(p)))
                    SubscribePane(pane);
            }
            OnPropertyChanged(nameof(ActivePane));
            UpdateWindowTitle();
        }
        catch (Exception ex)
        {
            AddToast(L10n.T("无法打开分屏"), ex.Message, ToastKind.Error);
        }
    }

    public async Task MountVolumeAsync(SidebarEntryViewModel entry)
    {
        var volume = FindVolume(entry);
        if (volume is null)
        {
            AddToast(L10n.T("无法挂载"), L10n.T("找不到设备"), ToastKind.Error);
            return;
        }

        var result = await VolumeService.MountAsync(volume).ConfigureAwait(true);
        await RefreshSidebarAsync().ConfigureAwait(true);
        if (!result.Success)
        {
            AddToast(L10n.T("挂载失败"), result.Message, ToastKind.Error);
            return;
        }

        AddToast(L10n.T("已挂载"), volume.Name, ToastKind.Success);
        var mounted = FindVolume(entry);
        if (mounted is { IsMounted: true } && !string.IsNullOrEmpty(mounted.MountPoint))
            await NavigateActiveAsync(_services.FileSystems.Local, mounted.MountPoint).ConfigureAwait(true);
    }

    public async Task UnmountVolumeAsync(SidebarEntryViewModel entry)
    {
        var volume = FindVolume(entry);
        if (volume is null || !volume.IsMounted)
        {
            AddToast(L10n.T("无法卸载"), L10n.T("找不到已挂载的设备"), ToastKind.Error);
            return;
        }

        var result = await VolumeService.UnmountAsync(volume).ConfigureAwait(true);
        if (!result.Success)
        {
            AddToast(L10n.T("卸载失败"), result.Message, ToastKind.Error);
            return;
        }

        await LeaveVolumeIfNeededAsync(volume.MountPoint).ConfigureAwait(true);
        await RefreshSidebarAsync().ConfigureAwait(true);
        AddToast(L10n.T("已卸载"), volume.Name, ToastKind.Success);
    }

    public async Task EjectVolumeAsync(SidebarEntryViewModel entry)
    {
        var volume = FindVolume(entry);
        if (volume is null || !volume.IsMounted)
        {
            AddToast(L10n.T("无法弹出"), L10n.T("找不到已挂载的设备"), ToastKind.Error);
            return;
        }

        var result = await VolumeService.EjectAsync(volume).ConfigureAwait(true);
        if (!result.Success)
        {
            AddToast(L10n.T("弹出失败"), result.Message, ToastKind.Error);
            return;
        }

        await LeaveVolumeIfNeededAsync(volume.MountPoint).ConfigureAwait(true);
        await RefreshSidebarAsync().ConfigureAwait(true);
        AddToast(L10n.T("已弹出"), volume.Name, ToastKind.Success);
    }

    private VolumeInfo? FindVolume(SidebarEntryViewModel entry)
        => _volumes.FirstOrDefault(volume =>
            string.Equals(volume.Device, entry.VolumeDevice, StringComparison.Ordinal));

    private async Task LeaveVolumeIfNeededAsync(string mountPoint)
    {
        if (string.IsNullOrEmpty(mountPoint))
            return;

        foreach (var pane in Tabs.SelectMany(tab => tab.Panes).ToList())
        {
            if (!pane.FileSystem.IsLocal)
                continue;
            var path = pane.CurrentPath;
            if (!path.Equals(mountPoint, StringComparison.Ordinal) &&
                !path.StartsWith(mountPoint.TrimEnd('/') + "/", StringComparison.Ordinal))
                continue;
            try
            {
                await pane.NavigateAsync(_services.FileSystems.Local,
                    KnownFolders.DefaultStartupPath).ConfigureAwait(true);
            }
            catch
            {
                // The pane will refresh when the user navigates elsewhere.
            }
        }
    }

    public Task OpenTerminalForSidebarEntryAsync(SidebarEntryViewModel entry, string? terminalId = null)
    {
        var path = entry.VolumeMountPoint ?? entry.Path;
        if (string.IsNullOrEmpty(path))
            return Task.CompletedTask;

        var ok = string.IsNullOrWhiteSpace(terminalId)
            ? _services.Terminals.OpenDefault(path)
            : _services.Terminals.OpenById(terminalId, path);
        if (!ok)
            AddToast(L10n.T("无法打开终端"), L10n.T("未找到可用的终端程序"), ToastKind.Error);
        return Task.CompletedTask;
    }

    public Task OpenHerdrForSidebarEntryAsync(SidebarEntryViewModel entry)
    {
        var path = entry.VolumeMountPoint ?? entry.Path;
        if (string.IsNullOrEmpty(path))
            return Task.CompletedTask;

        var ok = _services.Terminals.OpenInHerdr(path);
        if (!ok)
            AddToast(L10n.T("无法打开 Herdr"), L10n.T("请确认已安装 herdr，并且当前终端支持运行命令"), ToastKind.Error);
        return Task.CompletedTask;
    }

    public async Task RunCustomCommandForSidebarEntryAsync(SidebarEntryViewModel entry, string commandId)
    {
        var path = entry.VolumeMountPoint ?? entry.Path;
        var command = _services.CustomCommands.Find(commandId);
        if (string.IsNullOrEmpty(path) || command is null)
            return;

        if (!await _services.CustomCommands.RunAsync(command, path, []).ConfigureAwait(true))
            AddToast(L10n.T("无法运行命令"), command.Name, ToastKind.Error);
    }

    public Task AnalyzeDiskUsageForSidebarEntryAsync(SidebarEntryViewModel entry)
    {
        var path = entry.VolumeMountPoint ?? entry.Path;
        return string.IsNullOrEmpty(path)
            ? Task.CompletedTask
            : _services.Dialogs.ShowDiskUsageAsync(path);
    }

    public Task FindDuplicatesForSidebarEntryAsync(SidebarEntryViewModel entry)
    {
        var path = entry.VolumeMountPoint ?? entry.Path;
        return string.IsNullOrEmpty(path)
            ? Task.CompletedTask
            : _services.Dialogs.ShowDuplicateFinderAsync(path);
    }

    private async Task<IFileSystem?> ResolveSidebarFileSystemAsync(SidebarEntryViewModel entry)
    {
        if (entry.FileSystemId is null or "local")
            return _services.FileSystems.Local;
        if (entry.FileSystemId == "trash")
            return _services.TrashFs;

        var profile = _services.FileSystems.Connections.Get(entry.FileSystemId);
        try
        {
            var fs = await _services.FileSystems.ConnectByIdAsync(entry.FileSystemId).ConfigureAwait(true);
            if (profile is { LastError: not null })
            {
                profile.LastError = null;
                _services.FileSystems.Connections.Save(profile);
            }
            return fs;
        }
        catch (Exception ex)
        {
            if (profile is not null)
            {
                profile.LastError = ex.Message;
                _services.FileSystems.Connections.Save(profile);
            }
            throw;
        }
    }

    public async Task NavigateActiveAsync(IFileSystem fileSystem, string path, bool newTab = false)
    {
        if (newTab || ActivePane is null)
        {
            await CreateTabAsync(fileSystem, path, true).ConfigureAwait(true);
            return;
        }

        await ActivePane.NavigateAsync(fileSystem, path).ConfigureAwait(true);
        UpdateWindowTitle();
        UpdateSidebarActive();
    }

    public async Task RefreshAllPanesAsync()
    {
        foreach (var pane in Tabs.SelectMany(t => t.Panes).ToList())
        {
            try
            {
                await pane.RefreshAsync().ConfigureAwait(true);
            }
            catch { /* keep refreshing the others */ }
        }
    }

    public async Task ShowSettingsAsync()
    {
        if (_settingsDialogOpen)
            return;

        _settingsDialogOpen = true;
        try
        {
            var previous = _services.Settings.Current;
            var result = await _services.Dialogs.ShowSettingsAsync(previous).ConfigureAwait(true);
            if (result is null)
                return;

            _services.Settings.Replace(result);
            L10n.SetLanguage(result.Language);
            ThemeService.Apply(_services.Settings.Current);
            foreach (var window in _services.Windows.Windows)
                ThemeService.ApplyDensity(window, _services.Settings.Current.CompactMode);

            SidebarWidth = _services.Settings.Current.SidebarWidth;
            IsSidebarVisible = _services.Settings.Current.ShowSidebar;

            foreach (var pane in Tabs.SelectMany(t => t.Panes).ToList())
            {
                pane.ViewMode = _services.Settings.Current.View;
                pane.FoldersFirst = _services.Settings.Current.FoldersFirst;
            }

            await RefreshSidebarAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(ActivePane));
        }
        finally
        {
            _settingsDialogOpen = false;
        }
    }

    /// <summary>Drop onto a sidebar folder: copy or move the sources there.</summary>
    public async Task EnqueueDropTransferAsync(IReadOnlyList<OperationSource> sources,
        IFileSystem destinationFileSystem, string destinationPath, bool move)
    {
        if (sources.Count == 0)
            return;

        var preview = await PreviewTransferAsync(sources, destinationFileSystem, destinationPath, move)
            .ConfigureAwait(true);
        if (preview == ConflictChoice.Cancel)
            return;

        _services.Operations.Copy(sources, destinationFileSystem, destinationPath, move, preview);
        MaybeAutoShowTransferPanel();
        AddToast(move ? L10n.T("正在移动") : L10n.T("正在复制"),
            L10n.Format("{0} 个项目", sources.Count), ToastKind.Info);
    }

    private async Task<ConflictChoice?> PreviewTransferAsync(IReadOnlyList<OperationSource> sources,
        IFileSystem destinationFileSystem, string destinationDirectory, bool move)
    {
        if (!_services.Settings.Current.ShowTransferPreview || sources.Count < 2)
            return null;

        var conflicts = new List<string>();
        var scanned = 0;
        foreach (var source in sources.Take(500))
        {
            scanned++;
            try
            {
                var target = destinationFileSystem.Combine(destinationDirectory, source.Name);
                if (await destinationFileSystem.ExistsAsync(target).ConfigureAwait(true))
                    conflicts.Add(source.Name);
            }
            catch
            {
                // Let the per-file conflict resolver report probe failures.
            }
        }

        if (conflicts.Count == 0)
            return null;

        var choice = await _services.Dialogs.ShowTransferPreviewAsync(new TransferPreviewRequest(
            move, sources.Count, destinationFileSystem.GetDisplayPath(destinationDirectory),
            conflicts.Count, conflicts.Take(12).ToList(), scanned)).ConfigureAwait(true);
        return choice ?? ConflictChoice.Cancel;
    }

    /// <summary>Shift+drop onto the Trash entry: delete the sources permanently.</summary>
    public async Task DeleteDroppedSourcesPermanentlyAsync(IReadOnlyList<OperationSource> sources)
    {
        if (sources.Count == 0)
            return;
        if (sources.Any(s => s.FileSystem is TrashFileSystem))
            return;

        if (_services.Settings.Current.ConfirmPermanentDelete)
        {
            var ok = await _services.Dialogs.ConfirmAsync(
                L10n.T("永久删除这些项目？"),
                L10n.Format("将永久删除 {0} 个项目，此操作无法撤销。", sources.Count),
                L10n.T("永久删除"),
                true).ConfigureAwait(true);
            if (!ok)
                return;
        }

        _services.Operations.Delete(sources, permanent: true);
        MaybeAutoShowTransferPanel();
        AddToast(L10n.T("正在删除"), L10n.Format("{0} 个项目", sources.Count), ToastKind.Info);
    }

    /// <summary>Drop onto the Trash entry: move local sources to the XDG trash.</summary>
    public void MoveDroppedSourcesToTrash(IReadOnlyList<OperationSource> sources)
    {
        if (sources.Count == 0)
            return;

        if (!_services.Trash.IsSupported ||
            sources.Any(s => !s.FileSystem.IsLocal || s.FileSystem is TrashFileSystem))
        {
            AddToast(L10n.T("无法移到回收站"), L10n.T("当前平台不支持回收站"), ToastKind.Info);
            return;
        }

        _services.Operations.Delete(sources, permanent: false);
        MaybeAutoShowTransferPanel();
        AddToast(L10n.T("正在移到回收站"),
            L10n.Format("{0} 个项目", sources.Count), ToastKind.Info);
    }

    public void AddToast(string title, string message, ToastKind kind = ToastKind.Info)
    {
        var toast = new ToastViewModel { Title = title, Message = message, Kind = kind };
        Toasts.Add(toast);
        while (Toasts.Count > 6)
            Toasts.RemoveAt(0);

        _ = DismissToastLaterAsync(toast, kind == ToastKind.Error ? TimeSpan.FromSeconds(9) : TimeSpan.FromSeconds(4));
    }

    public void DismissToast(ToastViewModel toast)
    {
        Toasts.Remove(toast);
    }

    private async Task DismissToastLaterAsync(ToastViewModel toast, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => Toasts.Remove(toast));
    }

    public IReadOnlyList<SavedWorkspace> Workspaces => _services.Workspaces.All;

    public async Task SaveWorkspaceAsync()
    {
        var suggested = L10n.Format("工作区 {0}", DateTime.Now.ToString("MM-dd HH:mm"));
        var name = await _services.Dialogs.PromptAsync(L10n.T("保存工作区"), L10n.T("名称"), suggested)
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
            return;

        _services.Workspaces.Save(name.Trim(), CreateSessionState());
        BuildPaletteCommands();
        AddToast(L10n.T("工作区已保存"), name.Trim(), ToastKind.Success);
    }

    public async Task RestoreWorkspaceAsync(SavedWorkspace workspace)
    {
        if (workspace.State.Tabs.Count == 0)
            return;
        var confirmed = await _services.Dialogs.ConfirmAsync(
            L10n.T("恢复工作区？"),
            L10n.Format("将把 {0} 个标签页追加到当前窗口。", workspace.State.Tabs.Count),
            L10n.T("恢复")).ConfigureAwait(true);
        if (!confirmed)
            return;

        await RestoreSessionStateAsync(workspace.State, activateFirstNew: true).ConfigureAwait(true);
        AddToast(L10n.T("工作区已恢复"), workspace.Name, ToastKind.Success);
    }

    public async Task DeleteWorkspaceAsync(SavedWorkspace workspace)
    {
        var confirmed = await _services.Dialogs.ConfirmAsync(
            L10n.T("删除工作区？"), workspace.Name, L10n.T("删除"), destructive: true).ConfigureAwait(true);
        if (!confirmed)
            return;
        if (_services.Workspaces.Delete(workspace.Name))
        {
            BuildPaletteCommands();
            AddToast(L10n.T("工作区已删除"), workspace.Name, ToastKind.Info);
        }
    }

    public async Task ReplaceWithSessionAsync(SessionState state)
    {
        foreach (var tab in Tabs.ToList())
        {
            foreach (var pane in tab.Panes.ToList())
                UnsubscribePane(pane);
            tab.Dispose();
        }
        Tabs.Clear();
        ActiveTab = null;
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(CanCloseActiveTab));

        await RestoreSessionStateAsync(state).ConfigureAwait(true);
        if (Tabs.Count == 0)
            await CreateTabAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath, true)
                .ConfigureAwait(true);
        UpdateWindowTitle();
    }

    public async Task ReopenClosedWindowAsync()
    {
        var reopened = await _services.Windows.ReopenLastClosedWindowAsync().ConfigureAwait(true);
        AddToast(reopened ? L10n.T("已恢复窗口") : L10n.T("没有可恢复的窗口"),
            reopened ? L10n.T("上次关闭的窗口已重新打开") : L10n.T("关闭一个窗口后可以在这里恢复"),
            reopened ? ToastKind.Success : ToastKind.Info);
    }

    public SessionState CreateSessionState()
    {
        var state = new SessionState
        {
            ActiveTab = ActiveTab is null ? 0 : Tabs.IndexOf(ActiveTab),
        };

        foreach (var tab in Tabs)
        {
            var panes = tab.Panes.ToList();
            state.Tabs.Add(new SessionTab
            {
                IsSplit = tab.IsSplit,
                IsPinned = tab.IsPinned,
                ActivePane = Math.Max(0, panes.IndexOf(tab.ActivePane)),
                Panes = panes.Select(SnapshotPane).ToList(),
            });
        }

        return state;
    }

    private static SessionPane SnapshotPane(PaneViewModel pane)
        => pane.FileSystem is ArchiveFileSystem archive
            ? new SessionPane
            {
                FileSystemId = "archive",
                ArchivePath = archive.ArchivePath,
                Path = pane.CurrentPath,
            }
            : new SessionPane
            {
                FileSystemId = pane.FileSystem.Id,
                Path = pane.CurrentPath,
            };

    public void SaveSession()
    {
        try
        {
            if (!_services.Settings.Current.RestoreSession)
            {
                _services.Session.Clear();
                return;
            }

            _services.Session.Save(CreateSessionState());
            _services.Recent.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn("保存会话失败", ex);
        }
    }

    public void SaveWindowState(double width, double height, bool maximized)
    {
        var settings = _services.Settings.Current;
        if (width > 400 && height > 300)
        {
            settings.WindowWidth = width;
            settings.WindowHeight = height;
        }
        settings.WindowMaximized = maximized;
        settings.SidebarWidth = SidebarWidth;
        settings.ShowSidebar = IsSidebarVisible;
        _services.Settings.Save();
    }

    public SessionTab SnapshotTab(TabViewModel tab)
    {
        var panes = tab.Panes.ToList();
        return new SessionTab
        {
            IsSplit = tab.IsSplit,
            IsPinned = tab.IsPinned,
            ActivePane = Math.Max(0, panes.IndexOf(tab.ActivePane)),
            Panes = panes.Select(SnapshotPane).ToList(),
        };
    }

    public async Task<TabViewModel?> AdoptTabAsync(SessionTab state)
    {
        var tab = await CreateTabFromSessionAsync(state, Tabs.Count).ConfigureAwait(true);
        if (tab is not null)
        {
            if (tab.IsPinned)
                MoveTab(tab, Math.Max(0, PinnedTabCount - 1));
            ActivateTab(tab);
            UpdateWindowTitle();
        }
        return tab;
    }

    private void RememberClosedTab(TabViewModel tab, int index)
    {
        var panes = tab.Panes.ToList();
        var state = SnapshotTab(tab);
        _closedTabs.Add(new ClosedTabState(state, index, panes.Select(pane => pane.FileSystem).ToList()));
        while (_closedTabs.Count > MaxClosedTabs)
            _closedTabs.RemoveAt(0);
    }

    private async Task<TabViewModel?> CreateTabFromSessionAsync(SessionTab tabState,
        int insertAt, IReadOnlyList<IFileSystem>? fileSystems = null)
    {
        var panes = new List<(IFileSystem Fs, string Path)>();
        for (var index = 0; index < tabState.Panes.Count; index++)
        {
            var paneState = tabState.Panes[index];
            var fs = fileSystems is not null && index < fileSystems.Count &&
                     (fileSystems[index].IsLocal || fileSystems[index] is ArchiveFileSystem)
                ? fileSystems[index]
                : await ResolveSessionFileSystemAsync(paneState).ConfigureAwait(true);
            if (fs is null)
                continue;
            var path = string.IsNullOrWhiteSpace(paneState.Path) ? fs.RootPath : paneState.Path;
            if (fs.IsLocal && !Directory.Exists(path))
                path = KnownFolders.DefaultStartupPath;
            panes.Add((fs, path));
        }

        if (panes.Count == 0)
            return null;

        try
        {
            var primary = new PaneViewModel(_services, panes[0].Fs, panes[0].Path);
            var tab = new TabViewModel(_services, primary) { IsPinned = tabState.IsPinned };
            SubscribeTab(tab);
            SubscribePane(primary);
            var target = Math.Clamp(insertAt, 0, Tabs.Count);
            Tabs.Insert(target, tab);
            await primary.InitializeAsync().ConfigureAwait(true);

            if (panes.Count > 1 && tabState.IsSplit)
            {
                await tab.AddSecondaryPaneAsync(panes[1].Fs, panes[1].Path,
                    tabState.ActivePane == 1).ConfigureAwait(true);
                foreach (var pane in tab.Panes)
                    SubscribePane(pane);
            }

            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(CanCloseActiveTab));
            OnPropertyChanged(nameof(PinnedTabCount));
            UpdateWindowTitle();
            UpdateSidebarActive();
            return tab;
        }
        catch (Exception ex)
        {
            Log.Warn("恢复标签页失败", ex);
            return null;
        }
    }

    private async Task<bool> RestoreSessionAsync()
    {
        var session = _services.Session.Load();
        return session is not null && await RestoreSessionStateAsync(session).ConfigureAwait(true);
    }

    private async Task<bool> RestoreSessionStateAsync(SessionState session, bool activateFirstNew = false)
    {
        var firstNew = Tabs.Count;
        var restoredAny = false;

        foreach (var tabState in session.Tabs)
        {
            var tab = await CreateTabFromSessionAsync(tabState, Tabs.Count).ConfigureAwait(true);
            if (tab is not null)
                restoredAny = true;
        }

        // Keep pinned tabs ahead of regular ones even for older sessions.
        var ordered = Tabs.Where(tab => tab.IsPinned)
            .Concat(Tabs.Where(tab => !tab.IsPinned))
            .ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var current = Tabs.IndexOf(ordered[index]);
            if (current != index)
                Tabs.Move(current, index);
        }

        if (!restoredAny)
            return false;

        ActiveTab = activateFirstNew && firstNew < Tabs.Count
            ? Tabs[firstNew]
            : session.ActiveTab > 0 && session.ActiveTab < Tabs.Count
                ? Tabs[session.ActiveTab]
                : Tabs[0];
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(CanCloseActiveTab));
        UpdateWindowTitle();
        return true;
    }

    private async Task<IFileSystem?> ResolveSessionFileSystemAsync(SessionPane pane)
    {
        if (pane.FileSystemId is "local" or "")
            return _services.FileSystems.Local;
        if (pane.FileSystemId == "trash")
            return _services.TrashFs;
        if (pane.FileSystemId == "archive")
        {
            if (string.IsNullOrWhiteSpace(pane.ArchivePath) || !File.Exists(pane.ArchivePath))
                return null;
            try
            {
                return await ArchiveFileSystem.OpenAsync(pane.ArchivePath).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warn($"恢复压缩包失败: {pane.ArchivePath}", ex);
                return null;
            }
        }

        var profile = _services.FileSystems.Connections.Get(pane.FileSystemId);
        try
        {
            StartupStatus = L10n.T("正在重新连接远程位置…");
            var fileSystem = await _services.FileSystems.ConnectByIdAsync(pane.FileSystemId).ConfigureAwait(true);
            if (profile is { LastError: not null })
            {
                profile.LastError = null;
                _services.FileSystems.Connections.Save(profile);
            }
            return fileSystem;
        }
        catch (Exception ex)
        {
            if (profile is not null)
            {
                profile.LastError = ex.Message;
                _services.FileSystems.Connections.Save(profile);
            }
            Log.Warn($"恢复远程连接失败: {pane.FileSystemId}", ex);
            AddToast(L10n.T("无法恢复远程连接"), L10n.T("已跳过该标签页，远程会话可能已失效。"), ToastKind.Error);
            return null;
        }
    }

    private string? GetStartupLocation()
    {
        foreach (var argument in Program.StartupArguments)
        {
            if (string.IsNullOrWhiteSpace(argument) || argument.StartsWith('-'))
                continue;
            if (argument.Contains("://", StringComparison.Ordinal))
                return argument;
            if (File.Exists(argument) || Directory.Exists(argument))
                return argument;
        }
        return null;
    }

    private async Task<TabViewModel> CreateTabForLocationAsync(string location, bool activate,
        IReadOnlyList<string>? selection = null)
    {
        if (location.Contains("://", StringComparison.Ordinal))
        {
            var tab = await CreateTabAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath, activate)
                .ConfigureAwait(true);
            if (tab.ActivePane is { } pane)
                await pane.ResolveLocationAsync(location).ConfigureAwait(true);
            return tab;
        }

        var full = Path.GetFullPath(location);
        if (File.Exists(full))
        {
            var parent = Path.GetDirectoryName(full) ?? KnownFolders.Home;
            var tab = await CreateTabAsync(_services.FileSystems.Local, parent, activate).ConfigureAwait(true);
            if (tab.ActivePane is { } pane)
                pane.SelectPaths(selection is { Count: > 0 } ? selection : [full]);
            return tab;
        }

        var directoryTab = await CreateTabAsync(_services.FileSystems.Local, full, activate).ConfigureAwait(true);
        if (selection is { Count: > 0 } && directoryTab.ActivePane is { } directoryPane)
            directoryPane.SelectPaths(selection);
        return directoryTab;
    }

    private async Task RefreshSidebarAsync()
    {
        // ConnectionStore.Changed can fire on an SSH worker thread after host
        // key pinning. Icon brushes are thread-affine, so build on the UI thread.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => RefreshSidebarAsync()).ConfigureAwait(true);
            return;
        }

        if (_refreshingSidebar)
            return;
        _refreshingSidebar = true;
        try
        {
            _volumes.Clear();
            _volumes.AddRange(await VolumeService.GetVolumesAsync(
                _services.Settings.Current.ShowHiddenVolumes).ConfigureAwait(true));
            BuildSidebar();
        }
        finally
        {
            _refreshingSidebar = false;
        }
    }

    private void BuildSidebar()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(BuildSidebar);
            return;
        }

        SidebarSections.Clear();

        var accentIcon = ThemeService.Brush("AccentBrush", "#17937F");
        var neutralIcon = ThemeService.Brush("TextTertiaryBrush", "#8B958F");

        var places = new SidebarSectionViewModel { Title = L10n.T("位置") };
        var hiddenPlaces = _services.Settings.Current.HiddenPlaces;
        foreach (var place in KnownFolders.GetPlaces()
                     .Where(place => !hiddenPlaces.Contains(place.Key, StringComparer.OrdinalIgnoreCase)))
        {
            places.Items.Add(new SidebarEntryViewModel
            {
                Label = place.Label,
                Kind = SidebarEntryKind.Place,
                Icon = FileIconCatalog.Resource(PlaceIcon(place.Key)),
                IconBrush = place.Key == "home" ? accentIcon : neutralIcon,
                FileSystemId = "local",
                Path = place.Path,
                ActivateAsync = ActivateSidebarEntryAsync,
            });
        }

        foreach (var path in _services.Settings.Current.CustomSidebarPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                continue;
            if (places.Items.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var label = Path.GetFileName(path.TrimEnd('/', '\\'));
            if (string.IsNullOrWhiteSpace(label))
                label = path;

            places.Items.Add(new SidebarEntryViewModel
            {
                Label = label,
                Detail = KnownFolders.ShortenForDisplay(path),
                Kind = SidebarEntryKind.Place,
                Icon = FileIconCatalog.Resource("IconFolder"),
                IconBrush = neutralIcon,
                FileSystemId = "local",
                Path = path,
                ActivateAsync = ActivateSidebarEntryAsync,
            });
        }

        if (places.Items.Count > 0)
            SidebarSections.Add(places);

        var bookmarks = new SidebarSectionViewModel
        {
            Title = L10n.T("书签"),
            HeaderAction = AddCurrentBookmarkAsync,
            HeaderIcon = FileIconCatalog.Resource("IconPlus"),
            HeaderTooltip = L10n.T("添加当前文件夹"),
        };
        foreach (var bookmark in _services.Bookmarks.GetAll())
        {
            bookmarks.Items.Add(new SidebarEntryViewModel
            {
                Label = bookmark.Name,
                Detail = KnownFolders.ShortenForDisplay(bookmark.Path),
                Kind = SidebarEntryKind.Bookmark,
                Icon = FileIconCatalog.Resource("IconBookmark"),
                IconBrush = SidebarEntryViewModel.Brush("#C08A3E"),
                FileSystemId = bookmark.FileSystemId,
                Path = bookmark.Path,
                BookmarkId = bookmark.Id,
                ActivateAsync = ActivateSidebarEntryAsync,
            });
        }
        SidebarSections.Add(bookmarks);

        var hiddenVolumes = new HashSet<string>(
            _services.Settings.Current.HiddenVolumes ?? [], StringComparer.OrdinalIgnoreCase);
        var visibleVolumes = _volumes
            .Where(volume => !hiddenVolumes.Contains(volume.MountPoint))
            .ToList();
        if (_services.Settings.Current.ShowVolumes && visibleVolumes.Count > 0)
        {
            var volumes = new SidebarSectionViewModel { Title = L10n.T("磁盘") };
            foreach (var volume in visibleVolumes)
            {
                volumes.Items.Add(new SidebarEntryViewModel
                {
                    Label = volume.IsSystem ? L10n.T("系统") : volume.Name,
                    Detail = volume.SizeText,

                    Kind = SidebarEntryKind.Volume,
                    Icon = FileIconCatalog.Resource(volume.IsNetwork ? "IconServer" : "IconDrive"),
                    IconBrush = SidebarEntryViewModel.Brush(volume.IsNetwork ? "#5A9BD5" : "#7C8A99"),
                    FileSystemId = "local",
                    Path = volume.MountPoint,
                    VolumeMountPoint = volume.MountPoint,
                    VolumeDevice = volume.Device,
                    VolumeMounted = volume.IsMounted,
                    CanMountVolume = volume.CanMount,
                    CanUnmountVolume = volume.CanUnmount,
                    CanEjectVolume = volume.CanEject,
                    ShowUsage = volume.UsedFraction is not null,
                    UsageFraction = volume.UsedFraction ?? 0,
                    ActivateAsync = ActivateSidebarEntryAsync,
                });
            }
            SidebarSections.Add(volumes);
        }

        var network = new SidebarSectionViewModel
        {
            Title = L10n.T("网络"),
            HeaderAction = () => ShowConnectionDialogAsync(null),
            HeaderIcon = FileIconCatalog.Resource("IconPlus"),
            HeaderTooltip = L10n.T("添加远程连接"),
        };
        var hiddenConnections = _services.Settings.Current.HiddenConnections
            .ToHashSet(StringComparer.Ordinal);
        foreach (var connection in _services.FileSystems.Connections.GetAll())
        {
            if (hiddenConnections.Contains(connection.Id))
                continue;

            var failed = !string.IsNullOrEmpty(connection.LastError);
            network.Items.Add(new SidebarEntryViewModel
            {
                Label = connection.Name,
                Detail = failed
                    ? $"{connection.ProtocolLabel} · {connection.DisplayTarget} · {L10n.T("连接失败")}"
                    : $"{connection.ProtocolLabel} · {connection.DisplayTarget}",
                Kind = SidebarEntryKind.Connection,
                Icon = FileIconCatalog.Resource("IconCloud"),
                IconBrush = SidebarEntryViewModel.Brush(failed ? "#C26A6A" : "#5A9BD5"),
                ConnectionId = connection.Id,
                FailureMessage = connection.LastError,
                FileSystemId = connection.Id,
                Path = connection.NormalizeInitialPath(connection.InitialPath),
                ActivateAsync = ActivateSidebarEntryAsync,
            });
        }
        SidebarSections.Add(network);

        var recent = _services.Recent.GetAll().Take(5).ToList();
        if (recent.Count > 0)
        {
            var recents = new SidebarSectionViewModel { Title = L10n.T("最近") };
            foreach (var item in recent)
            {
                recents.Items.Add(new SidebarEntryViewModel
                {
                    Label = KnownFolders.GetDisplayName(item.Path) ?? item.Name,
                    Detail = KnownFolders.ShortenForDisplay(item.Path),
                    Kind = SidebarEntryKind.Recent,
                    Icon = FileIconCatalog.Resource("IconClock"),
                    IconBrush = SidebarEntryViewModel.Brush("#7D8794"),
                    FileSystemId = item.FileSystemId,
                    Path = item.Path,
                    ActivateAsync = ActivateSidebarEntryAsync,
                });
            }
            SidebarSections.Add(recents);
        }

        var system = new SidebarSectionViewModel { Title = L10n.T("系统") };
        system.Items.Add(new SidebarEntryViewModel
        {
            Label = L10n.T("回收站"),
            Detail = L10n.T("已删除的项目"),
            Kind = SidebarEntryKind.Trash,
            Icon = FileIconCatalog.Resource("IconTrash"),
            IconBrush = SidebarEntryViewModel.Brush("#C26A6A"),
            FileSystemId = "trash",
            Path = "/",
            ActivateAsync = ActivateSidebarEntryAsync,
        });
        SystemSection = system;

        UpdateSidebarActive();
    }

    public void RefreshSidebar() => BuildSidebar();

    /// <summary>Clear the file-pane drop overlay when a drag moves to the sidebar.</summary>
    public void ClearPaneDropTargets()
    {
        foreach (var pane in Tabs.SelectMany(tab => tab.Panes))
            pane.SetDropTarget(false);
    }

    private void UpdateSidebarActive()
    {
        var pane = ActivePane;
        var entries = SidebarSections.SelectMany(s => s.Items).ToList();
        if (SystemSection is not null)
            entries.AddRange(SystemSection.Items);

        // Highlight only the deepest matching place/volume. Every path under the
        // home folder also matches Home, which made Home look permanently active.
        SidebarEntryViewModel? deepestLocal = null;
        var deepestLength = -1;
        if (pane is not null && pane.FileSystem.IsLocal)
        {
            foreach (var entry in entries)
            {
                if (entry.Kind is not (SidebarEntryKind.Place or SidebarEntryKind.Volume))
                    continue;

                var root = entry.Path ?? string.Empty;
                if (!IsSameOrUnder(pane.CurrentPath, root))
                    continue;

                var length = root.TrimEnd(Path.DirectorySeparatorChar).Length;
                if (length > deepestLength)
                {
                    deepestLength = length;
                    deepestLocal = entry;
                }
            }
        }

        foreach (var entry in entries)
        {
            var active = false;
            if (pane is not null)
            {
                switch (entry.Kind)
                {
                    case SidebarEntryKind.Place:
                    case SidebarEntryKind.Volume:
                        active = ReferenceEquals(entry, deepestLocal);
                        break;
                    case SidebarEntryKind.Bookmark:
                    case SidebarEntryKind.Recent:
                        active = entry.FileSystemId == pane.FileSystem.Id &&
                                 string.Equals(pane.CurrentPath, entry.Path, StringComparison.Ordinal);
                        break;
                    case SidebarEntryKind.Connection:
                        active = entry.ConnectionId == pane.FileSystem.Id;
                        break;
                    case SidebarEntryKind.Trash:
                        active = pane.FileSystem is TrashFileSystem;
                        break;
                }
            }
            entry.IsActive = active;
        }
    }

    private static bool IsSameOrUnder(string path, string root)
    {
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar);
        if (normalizedPath.Length == 0)
            normalizedPath = Path.DirectorySeparatorChar.ToString();
        if (normalizedRoot.Length == 0)
            normalizedRoot = Path.DirectorySeparatorChar.ToString();
        if (normalizedRoot == Path.DirectorySeparatorChar.ToString())
            return true;
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private void SubscribeTab(TabViewModel tab)
    {
        tab.PropertyChanged += OnTabPropertyChanged;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TabViewModel tab || !ReferenceEquals(tab, ActiveTab))
            return;
        if (e.PropertyName is nameof(TabViewModel.ActivePane) or nameof(TabViewModel.Title))
        {
            if (e.PropertyName == nameof(TabViewModel.ActivePane) && Preview.IsOpen)
                Preview.Close();
            OnPropertyChanged(nameof(ActivePane));
            if (IsPreviewPanelOpen)
                _ = RefreshPreviewPanelAsync();
            UpdateWindowTitle();
            UpdateSidebarActive();
        }
    }

    private void SubscribePane(PaneViewModel pane)
    {
        if (!_subscribedPanes.Add(pane))
            return;
        pane.NotificationRequested += OnPaneNotification;
        pane.OpenInNewTabRequested += OnOpenInNewTabRequested;
        pane.OperationStarted += OnOperationStarted;
        pane.PropertyChanged += OnPanePropertyChanged;
        pane.Navigated += OnPaneNavigated;
        pane.SelectionChanged += OnPaneSelectionChanged;
        pane.PreviewRequested += OnPanePreviewRequested;
    }

    private void UnsubscribePane(PaneViewModel pane)
    {
        if (!_subscribedPanes.Remove(pane))
            return;
        pane.NotificationRequested -= OnPaneNotification;
        pane.OpenInNewTabRequested -= OnOpenInNewTabRequested;
        pane.OperationStarted -= OnOperationStarted;
        pane.PropertyChanged -= OnPanePropertyChanged;
        pane.Navigated -= OnPaneNavigated;
        pane.SelectionChanged -= OnPaneSelectionChanged;
        pane.PreviewRequested -= OnPanePreviewRequested;
    }

    private void OnPaneNotification(string title, string message, bool error)
        => AddToast(title, message, error ? ToastKind.Error : ToastKind.Info);

    private async void OnOpenInNewTabRequested(PaneViewModel pane, IFileSystem fileSystem, string path)
    {
        try
        {
            await CreateTabAsync(fileSystem, path, true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddToast(L10n.T("无法打开标签页"), ex.Message, ToastKind.Error);
        }
    }

    private void OnOperationStarted(PaneViewModel pane)
    {
        RefreshTransferState();
        MaybeAutoShowTransferPanel();
    }

    /// <summary>
    /// Only expand the transfer queue for operations that keep running after a
    /// short delay. Moving a couple of small files should stay out of the way.
    /// </summary>
    private async void MaybeAutoShowTransferPanel()
    {
        if (IsTransferPanelOpen || !_services.Settings.Current.ShowTransferPanel)
            return;

        await Task.Delay(700).ConfigureAwait(true);

        if (IsTransferPanelOpen || !_services.Settings.Current.ShowTransferPanel)
            return;
        if (_services.Operations.Operations.Any(operation => operation.IsRunning))
            IsTransferPanelOpen = true;
    }

    private void OnPaneNavigated(PaneViewModel pane)
    {
        if (!ReferenceEquals(pane, ActivePane))
            return;
        if (Preview.IsOpen)
            Preview.Close();
        if (IsPreviewPanelOpen)
            _ = RefreshPreviewPanelAsync();
        RefreshRecentSection();
        UpdateSidebarActive();
        UpdateWindowTitle();
    }

    private void RefreshRecentSection()
    {
        var recent = _services.Recent.GetAll().Take(5).ToList();
        var section = SidebarSections.FirstOrDefault(s => s.Title == L10n.T("最近"));
        if (section is null)
        {
            if (recent.Count > 0)
                BuildSidebar();
            return;
        }

        section.Items.Clear();
        foreach (var item in recent)
        {
            section.Items.Add(new SidebarEntryViewModel
            {
                Label = item.Name,
                Detail = KnownFolders.ShortenForDisplay(item.Path),
                Kind = SidebarEntryKind.Recent,
                Icon = FileIconCatalog.Resource("IconClock"),
                IconBrush = SidebarEntryViewModel.Brush("#7D8794"),
                FileSystemId = item.FileSystemId,
                Path = item.Path,
                ActivateAsync = ActivateSidebarEntryAsync,
            });
        }
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneViewModel.Title) or nameof(PaneViewModel.CurrentPath) or
            nameof(PaneViewModel.DisplayPath) or nameof(PaneViewModel.IsActive))
        {
            if (ReferenceEquals(sender, ActivePane))
            {
                UpdateWindowTitle();
                UpdateSidebarActive();
            }
        }
    }

    private void UpdateWindowTitle()
    {
        var title = ActivePane?.Title;
        WindowTitle = string.IsNullOrWhiteSpace(title) ? "Ordoa" : $"{title} — Ordoa";
        OnPropertyChanged(nameof(ActivePane));
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSettingsChanged(sender, e));
            return;
        }

        OnPropertyChanged(nameof(ToolbarCommands));
        OnPropertyChanged(nameof(HasToolbarCommands));

        var showPreviewPanel = _services.Settings.Current.ShowPreviewPanel;
        if (_isPreviewPanelOpen != showPreviewPanel)
        {
            _isPreviewPanelOpen = showPreviewPanel;
            OnPropertyChanged(nameof(IsPreviewPanelOpen));
            if (showPreviewPanel)
                _ = RefreshPreviewPanelAsync();
            else
                PreviewPanel.Close();
        }

        NotifyPreviewPanelLayoutChanged();
        ThemeService.Apply(_services.Settings.Current);
        foreach (var window in _services.Windows.Windows)
        {
            ThemeService.ApplyDensity(window, _services.Settings.Current.CompactMode);
            ThemeService.ApplyWindowFrame(window, _services.Settings.Current.UseSystemWindowFrame);
        }

        OnPropertyChanged(nameof(UseSystemWindowFrame));
        OnPropertyChanged(nameof(IsImmersiveWindow));
        OnPropertyChanged(nameof(ShowSidebarBranding));
        OnPropertyChanged(nameof(ShowWindowCaptionButtons));
        OnPropertyChanged(nameof(ReserveMacOSTrafficLights));
        OnPropertyChanged(nameof(ReserveMacOSTrafficLightsInTitleBar));
        BuildPaletteCommands();
    }

    private void OnConnectionsChanged(object? sender, EventArgs e)
    {
        _ = RefreshSidebarAsync();
    }

    private void OnOperationsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (FileOperationItem item in e.NewItems)
                item.PropertyChanged += OnOperationItemChanged;
        }
        if (e.OldItems is not null)
        {
            foreach (FileOperationItem item in e.OldItems)
                item.PropertyChanged -= OnOperationItemChanged;
        }
        RefreshTransferState();
    }

    private void OnOperationItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshTransferState();
        if (e.PropertyName == nameof(FileOperationItem.State) &&
            sender is FileOperationItem { IsRunning: true })
            MaybeAutoShowTransferPanel();
    }

    private void RefreshTransferState()
    {
        HasTransfers = _services.Operations.Operations.Count > 0;
        ActiveTransferCount = _services.Operations.Operations.Count(o => o.CanCancel);
        OnPropertyChanged(nameof(TransferSummary));
    }

    private void OnOperationFinished(object? sender, FileOperationItem item)
    {
        RefreshTransferState();
        if (item.State == OperationState.Completed)
        {
            AddToast(L10n.T("操作完成"), item.Title, ToastKind.Success);
            if (item.Kind is OperationKind.Copy or OperationKind.Move or OperationKind.Delete or OperationKind.Trash or
                OperationKind.Restore or OperationKind.EmptyTrash)
            {
                _ = RefreshAfterOperationAsync();
            }
        }
        else if (item.State == OperationState.Failed)
        {
            AddToast(L10n.T("操作失败"), string.IsNullOrWhiteSpace(item.Error) ? item.Title : item.Error, ToastKind.Error);
            _ = RefreshAfterOperationAsync();
        }
        else if (item.State == OperationState.Cancelled)
        {
            AddToast(L10n.T("操作已取消"), item.Title, ToastKind.Info);
        }
    }

    private async Task RefreshAfterOperationAsync()
    {
        await Task.Delay(120).ConfigureAwait(true);
        await RefreshAllPanesAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelTransfer(FileOperationItem? item) => item?.Cancel();

    [RelayCommand]
    private void TogglePauseTransfer(FileOperationItem? item)
    {
        if (item is null)
            return;
        if (item.IsPausedState)
            item.Resume();
        else
            item.Pause();
        RefreshTransferState();
    }

    [RelayCommand]
    private void RetryTransfer(FileOperationItem? item)
    {
        if (item is null)
            return;
        var retried = _services.Operations.Retry(item);
        if (retried is null)
        {
            AddToast(L10n.T("无法重试"), L10n.T("只有失败或已取消的传输可以重试"), ToastKind.Info);
            return;
        }
        RefreshTransferState();
    }

    [RelayCommand]
    private Task ShowOperationHistoryAsync()
        => _services.Dialogs.ShowOperationHistoryAsync(_services.Operations);

    [RelayCommand]
    private void ClearTransfers()
    {
        _services.Operations.ClearFinished();
        RefreshTransferState();
    }

    private void FilterPalette()
    {
        var query = PaletteQuery.Trim();
        var settings = _services.Settings.Current;
        int Score(PaletteCommand command)
        {
            var score = command.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 1000 : 0;
            score += settings.PaletteUsage.GetValueOrDefault(command.Title) * 10;
            var recentIndex = settings.PaletteRecent.IndexOf(command.Title);
            if (recentIndex >= 0)
                score += Math.Max(1, 60 - recentIndex * 4);
            return score;
        }

        var matches = PaletteCommands
            .Where(c => query.Length == 0 ||
                        c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        (c.Subtitle?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .OrderByDescending(Score)
            .ThenBy(c => c.Title, StringComparer.CurrentCulture)
            .ToList();

        FilteredCommands.Clear();
        foreach (var item in matches)
            FilteredCommands.Add(item);
        SelectedPaletteCommand = FilteredCommands.FirstOrDefault();
    }

    private void OnLanguageChanged()
        => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshLocalization);

    private void RefreshLocalization()
    {
        UpdateWindowTitle();
        OnPropertyChanged(nameof(TransferSummary));
        BuildPaletteCommands();
        FilterPalette();
        BuildSidebar();
        UpdateSidebarActive();
        foreach (var pane in Tabs.SelectMany(t => t.Panes))
            pane.RefreshLocalization();
        if (Preview.IsOpen)
            _ = Preview.RefreshAsync();
        if (PreviewPanel.IsOpen)
            _ = PreviewPanel.RefreshAsync();
    }

    private void BuildPaletteCommands()
    {
        PaletteCommands.Clear();

        void Add(string title, string? subtitle, string? shortcut, string icon, Func<Task>? action)
        {
            PaletteCommands.Add(new PaletteCommand
            {
                Title = title,
                Subtitle = subtitle,
                Shortcut = shortcut,
                Icon = FileIconCatalog.Resource(icon),
                ExecuteAsync = action,
            });
        }

        Add(L10n.T("撤销"), L10n.T("撤销上一步文件操作"), _services.Shortcuts.GetGesture("undo"), "IconUndo", UndoAsync);
        Add(L10n.T("重做"), L10n.T("重做被撤销的文件操作"), _services.Shortcuts.GetGesture("redo"), "IconRedo", RedoAsync);
        Add(L10n.T("新建标签页"), L10n.T("打开一个新的文件浏览标签"), _services.Shortcuts.GetGesture("newTab"), "IconPlus", NewTabAsync);
        Add(L10n.T("新建窗口"), L10n.T("在新的窗口中打开文件浏览"), _services.Shortcuts.GetGesture("newWindow"), "IconPlus", NewWindowAsync);
        Add(L10n.T("复制标签页"), L10n.T("克隆当前标签页"), null, "IconCopy", DuplicateTabAsync);
        Add(L10n.T("恢复关闭的标签页"), L10n.T("重新打开最近关闭的标签页"), _services.Shortcuts.GetGesture("reopenTab"), "IconRestore", ReopenClosedTabAsync);
        Add(L10n.T("固定 / 取消固定标签页"), L10n.T("固定标签页并保持在标签栏前部"), null, "IconStar", () =>
        {
            TogglePinTab(ActiveTab);
            return Task.CompletedTask;
        });
        Add(L10n.T("关闭标签页"), L10n.T("关闭当前标签页"), _services.Shortcuts.GetGesture("closeTab"), "IconClose", () =>
        {
            CloseTab(ActiveTab);
            return Task.CompletedTask;
        });
        Add(L10n.T("切换分屏"), L10n.T("在当前标签页中显示两个面板"), _services.Shortcuts.GetGesture("toggleSplit"), "IconSplit", ToggleSplitAsync);
        Add(L10n.T("显示/隐藏侧栏"), L10n.T("切换左侧导航栏"), _services.Shortcuts.GetGesture("toggleSidebar"), "IconPanel", () =>
        {
            ToggleSidebar();
            return Task.CompletedTask;
        });
        Add(L10n.T("刷新"), L10n.T("重新读取当前文件夹"), _services.Shortcuts.GetGesture("refresh"), "IconRefresh", RefreshAllAsync);
        Add(L10n.T("主目录"), L10n.T("打开主目录"), "Alt+Home", "IconHome", OpenHomeAsync);
        Add(L10n.T("回收站"), L10n.T("查看已删除的项目"), null, "IconTrash", OpenTrashAsync);
        Add(L10n.T("新建文件夹"), L10n.T("在当前位置创建文件夹"), _services.Shortcuts.GetGesture("newFolder"), "IconPlusCircle", () => PaneAction(p => p.NewFolderAsync()));
        Add(L10n.T("新建空文件"), L10n.T("在当前位置创建空文件"), null, "IconFile", () => PaneAction(p => p.NewFileAsync()));
        Add(L10n.T("打开方式"), L10n.T("选择其他应用打开选中的文件"), _services.Shortcuts.GetGesture("openWith"), "IconApps", () => PaneAction(p => p.OpenWithAsync()));
        Add(L10n.T("重命名"), L10n.T("重命名选中的项目"), _services.Shortcuts.GetGesture("rename"), "IconRename", () => PaneAction(p => p.RenameAsync()));
        Add(L10n.T("批量重命名"), L10n.T("使用查找替换、序号和日期变量批量修改名称"), _services.Shortcuts.GetGesture("batchRename"), "IconRename", () => PaneAction(p => p.BatchRenameAsync()));
        Add(L10n.T("移到回收站"), L10n.T("删除选中的项目到回收站"), _services.Shortcuts.GetGesture("delete"), "IconTrash", () => PaneAction(p => p.DeleteSelectionAsync(false)));
        Add(L10n.T("永久删除"), L10n.T("永久删除选中的项目"), _services.Shortcuts.GetGesture("permanentDelete"), "IconDelete", () => PaneAction(p => p.DeleteSelectionAsync(true)));
        Add(L10n.T("复制"), L10n.T("复制选中的项目"), _services.Shortcuts.GetGesture("copy"), "IconCopy", () => PaneAction(p => p.CopySelectionAsync(false)));
        Add(L10n.T("剪切"), L10n.T("剪切选中的项目"), _services.Shortcuts.GetGesture("cut"), "IconCut", () => PaneAction(p => p.CopySelectionAsync(true)));
        Add(L10n.T("粘贴"), L10n.T("将剪贴板中的项目粘贴到此处"), _services.Shortcuts.GetGesture("paste"), "IconPaste", () => PaneAction(p => p.PasteAsync()));
        Add(L10n.T("全选"), L10n.T("选择当前视图中的所有项目"), _services.Shortcuts.GetGesture("selectAll"), "IconSelectAll", () =>
        {
            ActivePane?.RequestSelectAll();
            return Task.CompletedTask;
        });
        Add(L10n.T("列表视图"), L10n.T("以详细列表显示文件"), _services.Shortcuts.GetGesture("listView"), "IconList", () =>
        {
            SetView(ViewKind.List);
            return Task.CompletedTask;
        });
        Add(L10n.T("网格视图"), L10n.T("以图标网格显示文件"), _services.Shortcuts.GetGesture("gridView"), "IconGrid", () =>
        {
            SetView(ViewKind.Grid);
            return Task.CompletedTask;
        });
        Add(L10n.T("树视图"), L10n.T("在当前文件夹中展开子文件夹"), null, "IconList", () =>
        {
            ActivePane?.ToggleTreeCommand.Execute(null);
            return Task.CompletedTask;
        });
        Add(L10n.T("Miller 列视图"), L10n.T("按列浏览多级文件夹"), null, "IconPanel", () =>
        {
            ActivePane?.ToggleMillerCommand.Execute(null);
            return Task.CompletedTask;
        });
        Add(L10n.T("显示/隐藏隐藏文件"), L10n.T("切换点文件与隐藏属性"), _services.Shortcuts.GetGesture("toggleHidden"), "IconEye", () =>
        {
            ToggleHidden();
            return Task.CompletedTask;
        });
        Add(L10n.T("快速预览"), L10n.T("按空格预览选中的文件"), _services.Shortcuts.GetGesture("quickPreview"), "IconEye", TogglePreviewAsync);
        Add(L10n.T("预览面板"), L10n.T("显示或隐藏常驻预览面板"), _services.Shortcuts.GetGesture("previewPanel"), "IconEye", () =>
        {
            TogglePreviewPanel();
            return Task.CompletedTask;
        });
        Add(L10n.T("属性"), L10n.T("查看选中项目的详细信息"), "Alt+Enter", "IconInfo", () => PaneAction(p => p.PropertiesAsync()));
        Add(L10n.T("计算 SHA-256"), L10n.T("计算选中文件的校验和"), null, "IconCheckCircle", () => PaneAction(p => p.ChecksumAsync()));
        Add(L10n.T("磁盘占用分析"), L10n.T("用树图查看当前文件夹的空间占用"), null, "IconDisk", () => PaneAction(p => p.AnalyzeDiskUsageAsync()));
        Add(L10n.T("查找重复文件"), L10n.T("按内容查找当前文件夹中的重复文件"), null, "IconCopy", () => PaneAction(p => p.FindDuplicateFilesAsync()));
        Add(L10n.T("批量转换图片…"), L10n.T("转换格式、质量或最大尺寸"), null, "IconImage",
            () => PaneAction(p => p.ConvertImagesAsync()));
        Add(L10n.T("编辑标签…"), L10n.T("为选中的文件或文件夹设置标签"), null, "IconBookmark",
            () => PaneAction(p => p.EditTagsAsync()));
        Add(L10n.T("比较选中文件"), L10n.T("并排查看两个文本或二进制文件的差异"), null, "IconPanel",
            () => PaneAction(p => p.CompareSelectionAsync()));
        if (_services.Settings.Current.EnableGitIntegration)
        {
            Add(L10n.T("Git 状态"), L10n.T("查看当前仓库的分支与未提交变更"), null, "IconCode",
                () => PaneAction(p => p.ShowGitStatusAsync()));
        }
        Add(L10n.T("重新打开关闭的窗口"), L10n.T("恢复上次关闭窗口中的标签页"), null, "IconRestore",
            ReopenClosedWindowAsync);
        Add(L10n.T("保存工作区…"), L10n.T("保存当前标签页和分屏布局"), null, "IconBookmark",
            SaveWorkspaceAsync);
        foreach (var workspace in Workspaces)
        {
            var saved = workspace;
            Add(L10n.Format("工作区：{0}", saved.Name),
                L10n.Format("{0} · 恢复标签页", saved.SavedAt.ToLocalTime().ToString("MM-dd HH:mm")),
                null, "IconBookmark", () => RestoreWorkspaceAsync(saved));
            Add(L10n.Format("删除工作区：{0}", saved.Name), L10n.T("删除已保存的工作区"),
                null, "IconDelete", () => DeleteWorkspaceAsync(saved));
        }
        Add(L10n.T("创建符号链接"), L10n.T("在当前位置创建指向选中项目的链接"), null, "IconLink",
            () => PaneAction(p => p.CreateSymbolicLinkAsync(false)));
        Add(L10n.T("计算文件夹大小"), L10n.T("计算选中文件夹的递归大小"), null, "IconFolder",
            () => PaneAction(p => p.CalculateFolderSizesAsync()));
        Add(L10n.T("操作历史"), L10n.T("查看本次会话的复制、移动与删除记录"), null, "IconRestore",
            () => _services.Dialogs.ShowOperationHistoryAsync(_services.Operations));
        Add(L10n.T("撤销历史"), L10n.T("查看并执行可撤销和可重做的操作"), null, "IconUndo",
            () => _services.Dialogs.ShowUndoHistoryAsync(_services.Undo));
        Add(L10n.T("压缩为 ZIP"), L10n.T("将选中的项目压缩为 zip"), null, "IconArchive", () => PaneAction(p => p.CompressAsync(false)));
        Add(L10n.T("压缩为 tar.gz"), L10n.T("将选中的项目压缩为 tar.gz"), null, "IconArchive", () => PaneAction(p => p.CompressAsync(true)));
        Add(L10n.T("压缩为 7z"), L10n.T("使用 7z 压缩选中的项目"), null, "IconArchive", () => PaneAction(p => p.Compress7zAsync()));
        Add(L10n.T("解压到此处"), L10n.T("解压选中的压缩包"), null, "IconDownload", () => PaneAction(p => p.ExtractAsync()));
        Add(L10n.T("解压到…"), L10n.T("选择目标文件夹后解压"), null, "IconDownload", () => PaneAction(p => p.ExtractSelectionToAsync()));
        Add(L10n.T("以压缩包浏览"), L10n.T("像文件夹一样打开选中的压缩包"), null, "IconArchive", () => PaneAction(p => p.OpenArchiveAsync()));
        Add(L10n.T("在终端中打开"), L10n.T("在当前位置或选中的文件夹中启动终端"), null, "IconTerminal", () => PaneAction(p => p.OpenTerminalAsync()));
        if (_services.Terminals.GetEnabledHerdr() is not null)
        {
            Add(L10n.T("在 Herdr 中打开"), L10n.T("在当前文件夹中启动 Herdr 工作区"), null, "IconTerminal",
                () => PaneAction(p => p.OpenInHerdrAsync()));
        }
        foreach (var command in _services.CustomCommands.GetAll())
        {
            if (string.IsNullOrWhiteSpace(command.Command))
                continue;
            var id = command.Id;
            var name = string.IsNullOrWhiteSpace(command.Name) ? command.Command : command.Name;
            Add(name, L10n.T("自定义命令"), null,
                command.RunInTerminal ? "IconTerminal" : "IconCommand",
                () => PaneAction(p => p.RunCustomCommandAsync(id)));
        }
        Add(L10n.T("复制路径"), L10n.T("复制选中项目的完整路径"), null, "IconLink", () => PaneAction(p => p.CopyPathAsync()));
        Add(L10n.T("添加书签"), L10n.T("将当前文件夹加入侧栏"), _services.Shortcuts.GetGesture("bookmark"), "IconBookmark", AddBookmarkAsync);
        Add(L10n.T("新建远程连接"), L10n.T("连接到 FTP、SFTP、SMB 或 WebDAV"), null, "IconCloudAdd", () => ShowConnectionDialogAsync(null));
        Add(L10n.T("设置"), L10n.T("打开应用设置"), _services.Shortcuts.GetGesture("settings"), "IconSettings", OpenSettingsAsync);
        Add(L10n.T("键盘快捷键"), L10n.T("查看所有快捷键"), _services.Shortcuts.GetGesture("about"), "IconKeyboard", () => _services.Dialogs.ShowAboutAsync());
        Add(L10n.T("关闭分屏"), L10n.T("隐藏当前标签页的第二个面板"), null, "IconSplit", () =>
        {
            if (ActiveTab is { IsSplit: true })
            {
                ActiveTab.CloseSplit();
                OnPropertyChanged(nameof(ActivePane));
                UpdateWindowTitle();
            }
            return Task.CompletedTask;
        });
    }

    private Task PaneAction(Func<PaneViewModel, Task> action)
    {
        var pane = ActivePane;
        return pane is null ? Task.CompletedTask : action(pane);
    }

    private static string PlaceIcon(string key) => key switch
    {
        "home" => "IconHome",
        "desktop" => "IconDesktop",
        "documents" => "IconDocuments",
        "downloads" => "IconDownload",
        "pictures" => "IconPictures",
        "music" => "IconMusic",
        "videos" => "IconVideos",
        _ => "IconGlobe",
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ready.TrySetResult(false);
        _services.Settings.Changed -= OnSettingsChanged;
        L10n.LanguageChanged -= OnLanguageChanged;
        _services.Operations.Finished -= OnOperationFinished;
        _services.Operations.Operations.CollectionChanged -= OnOperationsChanged;
        _services.FileSystems.Connections.Changed -= OnConnectionsChanged;

        Preview.Dispose();
        PreviewPanel.Dispose();
        foreach (var tab in Tabs.ToList())
        {
            foreach (var pane in tab.Panes.ToList())
                UnsubscribePane(pane);
            tab.Dispose();
        }
        Tabs.Clear();
    }

    private sealed record ClosedTabState(
        SessionTab Tab,
        int Index,
        IReadOnlyList<IFileSystem> FileSystems);
}
