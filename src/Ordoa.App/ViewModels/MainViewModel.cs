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
    private readonly List<VolumeInfo> _volumes = [];
    private bool _initialized;
    private bool _disposed;
    private bool _refreshingSidebar;

    public MainViewModel(AppServices services)
    {
        _services = services;
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

    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    public ObservableCollection<SidebarSectionViewModel> SidebarSections { get; } = [];
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

            if (value is not null)
                value.IsActive = true;

            OnPropertyChanged(nameof(ActivePane));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(HasTabs));
            ActiveTabChanged?.Invoke(value);
            OnPropertyChanged(nameof(CanCloseActiveTab));
            UpdateWindowTitle();
            UpdateSidebarActive();
        }
    }

    public PaneViewModel? ActivePane => ActiveTab?.ActivePane;
    public bool HasTabs => Tabs.Count > 0;
    public bool CanCloseActiveTab => Tabs.Count > 0;

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

    public string TransferSummary => ActiveTransferCount > 0
        ? L10n.Format("{0} 个进行中", ActiveTransferCount)
        : HasTransfers ? L10n.T("传输队列") : L10n.T("没有传输任务");

    public event Action? CloseWindowRequested;
    public event Action? CommandPaletteOpened;
    public event Action<TabViewModel?>? ActiveTabChanged;

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        IsInitialized = false;
        StartupStatus = L10n.T("正在准备…");

        var startupLocation = GetStartupLocation();
        var restored = false;

        if (_services.Settings.Current.RestoreSession)
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
                    await CreateTabForLocationAsync(startupLocation, true).ConfigureAwait(true);
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
                await CreateTabForLocationAsync(startupLocation, true).ConfigureAwait(true);
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
    }

    public void EnsureInitialized()
    {
        _initialized = true;
        IsInitialized = true;
        StartupStatus = string.Empty;
        if (Tabs.Count == 0)
            _ = CreateTabAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath, true);
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
    private async Task DuplicateActiveTabAsync()
        => await DuplicateTabAsync().ConfigureAwait(true);

    [RelayCommand]
    public void CloseTab(TabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null)
            return;

        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;

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
            }
            catch (Exception ex)
            {
                AddToast(L10n.T("操作失败"), ex.Message, ToastKind.Error);
            }
        }
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
        try
        {
            var result = await _services.Dialogs.ShowConnectionAsync(preset).ConfigureAwait(true);
            if (result is null)
                return;

            var profile = result.Profile;
            var fs = await _services.FileSystems.ConnectAsync(profile, result.Password).ConfigureAwait(true);
            if (result.SaveProfile)
                await _services.FileSystems.SaveProfileAsync(profile, result.Password,
                    deletePassword: !profile.SavePassword).ConfigureAwait(true);
            await CreateTabAsync(fs, profile.NormalizeInitialPath(profile.InitialPath), true).ConfigureAwait(true);
            await RefreshSidebarAsync().ConfigureAwait(true);
            AddToast(L10n.T("已连接"), $"{profile.ProtocolLabel} · {profile.DisplayTarget}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            AddToast(L10n.T("连接失败"), ex.Message, ToastKind.Error);
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

            var oldId = profile.Id;
            await _services.FileSystems.DisconnectAsync(oldId).ConfigureAwait(true);
            await _services.FileSystems.SaveProfileAsync(result.Profile, result.Password,
                deletePassword: !result.Profile.SavePassword).ConfigureAwait(true);

            var affected = Tabs.SelectMany(t => t.Panes).Where(p => p.FileSystem.Id == oldId).ToList();
            if (affected.Count > 0)
            {
                var newFs = await _services.FileSystems.ConnectAsync(result.Profile, result.Password).ConfigureAwait(true);
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

            RefreshSidebar();
            AddToast(L10n.T("连接已更新"), result.Profile.Name, ToastKind.Success);
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

        foreach (var pane in Tabs.SelectMany(t => t.Panes).ToList())
        {
            if (pane.FileSystem.Id == entry.ConnectionId)
            {
                try
                {
                    await pane.NavigateAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath).ConfigureAwait(true);
                }
                catch { /* ignore */ }
            }
        }

        RefreshSidebar();
        AddToast(L10n.T("已移除连接"), entry.Label, ToastKind.Success);
    }

    public async Task ActivateSidebarEntryAsync(SidebarEntryViewModel entry)
    {
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
                    var profile = _services.FileSystems.Connections.Get(entry.ConnectionId);
                    if (profile is null)
                        return;
                    var fs = await _services.FileSystems.ConnectByIdAsync(profile.Id).ConfigureAwait(true);
                    await NavigateActiveAsync(fs, profile.NormalizeInitialPath(profile.InitialPath)).ConfigureAwait(true);
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

    public async Task OpenTerminalForSidebarEntryAsync(SidebarEntryViewModel entry)
    {
        var path = entry.VolumeMountPoint ?? entry.Path;
        if (string.IsNullOrEmpty(path))
            return;
        var ok = await _services.Shell.OpenTerminalAsync(path).ConfigureAwait(true);
        if (!ok)
            AddToast(L10n.T("无法打开终端"), L10n.T("未找到可用的终端程序"), ToastKind.Error);
    }

    private async Task<IFileSystem?> ResolveSidebarFileSystemAsync(SidebarEntryViewModel entry)
    {
        if (entry.FileSystemId is null or "local")
            return _services.FileSystems.Local;
        if (entry.FileSystemId == "trash")
            return _services.TrashFs;
        return await _services.FileSystems.ConnectByIdAsync(entry.FileSystemId).ConfigureAwait(true);
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
        var previous = _services.Settings.Current;
        var result = await _services.Dialogs.ShowSettingsAsync(previous).ConfigureAwait(true);
        if (result is null)
            return;

        _services.Settings.Replace(result);
        L10n.SetLanguage(result.Language);
        ThemeService.Apply(_services.Settings.Current);
        if (_services.MainWindow is { } window)
            ThemeService.ApplyDensity(window, _services.Settings.Current.CompactMode);

        SidebarWidth = _services.Settings.Current.SidebarWidth;
        IsSidebarVisible = _services.Settings.Current.ShowSidebar;

        foreach (var pane in Tabs.SelectMany(t => t.Panes).ToList())
        {
            pane.ViewMode = _services.Settings.Current.View;
            pane.FoldersFirst = _services.Settings.Current.FoldersFirst;
        }

        RefreshSidebar();
        OnPropertyChanged(nameof(ActivePane));
    }

    /// <summary>Drop onto a sidebar folder: copy or move the sources there.</summary>
    public void EnqueueDropTransfer(IReadOnlyList<OperationSource> sources, IFileSystem destinationFileSystem,
        string destinationPath, bool move)
    {
        if (sources.Count == 0)
            return;
        _services.Operations.Copy(sources, destinationFileSystem, destinationPath, move);
        if (_services.Settings.Current.ShowTransferPanel)
            IsTransferPanelOpen = true;
        AddToast(move ? L10n.T("正在移动") : L10n.T("正在复制"),
            L10n.Format("{0} 个项目", sources.Count), ToastKind.Info);
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
        if (_services.Settings.Current.ShowTransferPanel)
            IsTransferPanelOpen = true;
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
        if (_services.Settings.Current.ShowTransferPanel)
            IsTransferPanelOpen = true;
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

    public void SaveSession()
    {
        try
        {
            if (!_services.Settings.Current.RestoreSession)
            {
                _services.Session.Clear();
                return;
            }

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
                    ActivePane = Math.Max(0, panes.IndexOf(tab.ActivePane)),
                    Panes = panes.Select(p => new SessionPane
                    {
                        FileSystemId = p.FileSystem.Id,
                        Path = p.CurrentPath,
                    }).ToList(),
                });
            }

            _services.Session.Save(state);
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

    private async Task<bool> RestoreSessionAsync()
    {
        var session = _services.Session.Load();
        if (session is null)
            return false;

        var restoredAny = false;

        foreach (var tabState in session.Tabs)
        {
            var panes = new List<(IFileSystem Fs, string Path)>();
            foreach (var paneState in tabState.Panes)
            {
                var fs = await ResolveSessionFileSystemAsync(paneState).ConfigureAwait(true);
                if (fs is null)
                    continue;
                var path = string.IsNullOrWhiteSpace(paneState.Path) ? fs.RootPath : paneState.Path;
                if (fs.IsLocal && !Directory.Exists(path))
                    path = KnownFolders.DefaultStartupPath;
                panes.Add((fs, path));
            }

            if (panes.Count == 0)
                continue;

            try
            {
                var primary = new PaneViewModel(_services, panes[0].Fs, panes[0].Path);
                var tab = new TabViewModel(_services, primary);
                SubscribeTab(tab);
                SubscribePane(primary);
                Tabs.Add(tab);
                await primary.InitializeAsync().ConfigureAwait(true);

                if (panes.Count > 1 && tabState.IsSplit)
                {
                    await tab.AddSecondaryPaneAsync(panes[1].Fs, panes[1].Path, tabState.ActivePane == 1).ConfigureAwait(true);
                    foreach (var pane in tab.Panes)
                        SubscribePane(pane);
                }

                restoredAny = true;
            }
            catch (Exception ex)
            {
                Log.Warn("恢复标签页失败", ex);
            }
        }

        if (!restoredAny)
            return false;

        ActiveTab = session.ActiveTab > 0 && session.ActiveTab < Tabs.Count ? Tabs[session.ActiveTab] : Tabs[0];
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

        try
        {
            StartupStatus = L10n.T("正在重新连接远程位置…");
            return await _services.FileSystems.ConnectByIdAsync(pane.FileSystemId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
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

    private async Task CreateTabForLocationAsync(string location, bool activate)
    {
        if (location.Contains("://", StringComparison.Ordinal))
        {
            var tab = await CreateTabAsync(_services.FileSystems.Local, KnownFolders.DefaultStartupPath, activate)
                .ConfigureAwait(true);
            if (tab.ActivePane is { } pane)
                await pane.ResolveLocationAsync(location).ConfigureAwait(true);
            return;
        }

        var full = Path.GetFullPath(location);
        if (File.Exists(full))
        {
            var parent = Path.GetDirectoryName(full) ?? KnownFolders.Home;
            await CreateTabAsync(_services.FileSystems.Local, parent, activate).ConfigureAwait(true);
        }
        else
        {
            await CreateTabAsync(_services.FileSystems.Local, full, activate).ConfigureAwait(true);
        }
    }

    private async Task RefreshSidebarAsync()
    {
        if (_refreshingSidebar)
            return;
        _refreshingSidebar = true;
        try
        {
            _volumes.Clear();
            _volumes.AddRange(await VolumeService.GetVolumesAsync().ConfigureAwait(true));
            BuildSidebar();
        }
        finally
        {
            _refreshingSidebar = false;
        }
    }

    private void BuildSidebar()
    {
        SidebarSections.Clear();

        var accentIcon = ThemeService.Brush("AccentBrush", "#17937F");
        var neutralIcon = ThemeService.Brush("TextTertiaryBrush", "#8B958F");

        var places = new SidebarSectionViewModel { Title = L10n.T("位置") };
        foreach (var place in KnownFolders.GetPlaces())
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
        if (places.Items.Count > 0)
            SidebarSections.Add(places);

        var bookmarks = new SidebarSectionViewModel { Title = L10n.T("书签") };
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
        bookmarks.Items.Add(new SidebarEntryViewModel
        {
            Label = L10n.T("添加当前文件夹"),
            Kind = SidebarEntryKind.AddBookmark,
            Icon = FileIconCatalog.Resource("IconPlusCircle"),
            IconBrush = accentIcon,
            ActivateAsync = ActivateSidebarEntryAsync,
        });
        SidebarSections.Add(bookmarks);

        if (_volumes.Count > 0)
        {
            var volumes = new SidebarSectionViewModel { Title = L10n.T("磁盘") };
            foreach (var volume in _volumes)
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
                    ShowUsage = volume.UsedFraction is not null,
                    UsageFraction = volume.UsedFraction ?? 0,
                    ActivateAsync = ActivateSidebarEntryAsync,
                });
            }
            SidebarSections.Add(volumes);
        }

        var network = new SidebarSectionViewModel { Title = L10n.T("网络") };
        foreach (var connection in _services.FileSystems.Connections.GetAll())
        {
            network.Items.Add(new SidebarEntryViewModel
            {
                Label = connection.Name,
                Detail = $"{connection.ProtocolLabel} · {connection.DisplayTarget}",
                Kind = SidebarEntryKind.Connection,
                Icon = FileIconCatalog.Resource("IconCloud"),
                IconBrush = SidebarEntryViewModel.Brush("#5A9BD5"),
                ConnectionId = connection.Id,
                FileSystemId = connection.Id,
                Path = connection.NormalizeInitialPath(connection.InitialPath),
                ActivateAsync = ActivateSidebarEntryAsync,
            });
        }
        network.Items.Add(new SidebarEntryViewModel
        {
            Label = L10n.T("添加远程连接"),
            Detail = "FTP · SFTP · SMB · WebDAV",
            Kind = SidebarEntryKind.AddConnection,
            Icon = FileIconCatalog.Resource("IconCloudAdd"),
            IconBrush = accentIcon,
            ActivateAsync = ActivateSidebarEntryAsync,
        });
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
        SidebarSections.Add(system);

        UpdateSidebarActive();
    }

    public void RefreshSidebar() => BuildSidebar();

    private void UpdateSidebarActive()
    {
        var pane = ActivePane;
        foreach (var entry in SidebarSections.SelectMany(s => s.Items))
        {
            var active = false;
            if (pane is not null)
            {
                switch (entry.Kind)
                {
                    case SidebarEntryKind.Place:
                    case SidebarEntryKind.Volume:
                        active = pane.FileSystem.IsLocal &&
                                 IsSameOrUnder(pane.CurrentPath, entry.Path ?? string.Empty);
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
            OnPropertyChanged(nameof(ActivePane));
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
        if (_services.Settings.Current.ShowTransferPanel)
            IsTransferPanelOpen = true;
        RefreshTransferState();
    }

    private void OnPaneNavigated(PaneViewModel pane)
    {
        if (!ReferenceEquals(pane, ActivePane))
            return;
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
        WindowTitle = string.IsNullOrWhiteSpace(title) ? "Ordoa 安序" : $"{title} — Ordoa 安序";
        OnPropertyChanged(nameof(ActivePane));
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ThemeService.Apply(_services.Settings.Current);
        if (_services.MainWindow is { } window)
            ThemeService.ApplyDensity(window, _services.Settings.Current.CompactMode);
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

    private void OnOperationItemChanged(object? sender, PropertyChangedEventArgs e) => RefreshTransferState();

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
    private void ClearTransfers()
    {
        _services.Operations.ClearFinished();
        RefreshTransferState();
    }

    private void FilterPalette()
    {
        var query = PaletteQuery.Trim();
        var matches = PaletteCommands
            .Where(c => query.Length == 0 ||
                        c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        (c.Subtitle?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .OrderByDescending(c => c.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
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

        Add(L10n.T("新建标签页"), L10n.T("打开一个新的文件浏览标签"), "Ctrl+T", "IconPlus", NewTabAsync);
        Add(L10n.T("复制标签页"), L10n.T("克隆当前标签页"), null, "IconCopy", DuplicateTabAsync);
        Add(L10n.T("关闭标签页"), L10n.T("关闭当前标签页"), "Ctrl+W", "IconClose", () =>
        {
            CloseTab(ActiveTab);
            return Task.CompletedTask;
        });
        Add(L10n.T("切换分屏"), L10n.T("在当前标签页中显示两个面板"), "Ctrl+\\", "IconSplit", ToggleSplitAsync);
        Add(L10n.T("显示/隐藏侧栏"), L10n.T("切换左侧导航栏"), "F9", "IconPanel", () =>
        {
            ToggleSidebar();
            return Task.CompletedTask;
        });
        Add(L10n.T("刷新"), L10n.T("重新读取当前文件夹"), "F5", "IconRefresh", RefreshAllAsync);
        Add(L10n.T("主目录"), L10n.T("打开主目录"), "Alt+Home", "IconHome", OpenHomeAsync);
        Add(L10n.T("回收站"), L10n.T("查看已删除的项目"), null, "IconTrash", OpenTrashAsync);
        Add(L10n.T("新建文件夹"), L10n.T("在当前位置创建文件夹"), "Ctrl+Shift+N", "IconPlusCircle", () => PaneAction(p => p.NewFolderAsync()));
        Add(L10n.T("新建空文件"), L10n.T("在当前位置创建空文件"), null, "IconFile", () => PaneAction(p => p.NewFileAsync()));
        Add(L10n.T("重命名"), L10n.T("重命名选中的项目"), "F2", "IconRename", () => PaneAction(p => p.RenameAsync()));
        Add(L10n.T("移到回收站"), L10n.T("删除选中的项目到回收站"), "Delete", "IconTrash", () => PaneAction(p => p.DeleteSelectionAsync(false)));
        Add(L10n.T("永久删除"), L10n.T("永久删除选中的项目"), "Shift+Delete", "IconDelete", () => PaneAction(p => p.DeleteSelectionAsync(true)));
        Add(L10n.T("复制"), L10n.T("复制选中的项目"), "Ctrl+C", "IconCopy", () => PaneAction(p => p.CopySelectionAsync(false)));
        Add(L10n.T("剪切"), L10n.T("剪切选中的项目"), "Ctrl+X", "IconCut", () => PaneAction(p => p.CopySelectionAsync(true)));
        Add(L10n.T("粘贴"), L10n.T("将剪贴板中的项目粘贴到此处"), "Ctrl+V", "IconPaste", () => PaneAction(p => p.PasteAsync()));
        Add(L10n.T("全选"), L10n.T("选择当前视图中的所有项目"), "Ctrl+A", "IconSelectAll", () =>
        {
            ActivePane?.RequestSelectAll();
            return Task.CompletedTask;
        });
        Add(L10n.T("列表视图"), L10n.T("以详细列表显示文件"), "Ctrl+1", "IconList", () =>
        {
            SetView(ViewKind.List);
            return Task.CompletedTask;
        });
        Add(L10n.T("网格视图"), L10n.T("以图标网格显示文件"), "Ctrl+2", "IconGrid", () =>
        {
            SetView(ViewKind.Grid);
            return Task.CompletedTask;
        });
        Add(L10n.T("显示/隐藏隐藏文件"), L10n.T("切换点文件与隐藏属性"), "Ctrl+H", "IconEye", () =>
        {
            ToggleHidden();
            return Task.CompletedTask;
        });
        Add(L10n.T("属性"), L10n.T("查看选中项目的详细信息"), "Alt+Enter", "IconInfo", () => PaneAction(p => p.PropertiesAsync()));
        Add(L10n.T("计算 SHA-256"), L10n.T("计算选中文件的校验和"), null, "IconCheckCircle", () => PaneAction(p => p.ChecksumAsync()));
        Add(L10n.T("压缩为 ZIP"), L10n.T("将选中的项目压缩为 zip"), null, "IconArchive", () => PaneAction(p => p.CompressAsync(false)));
        Add(L10n.T("压缩为 tar.gz"), L10n.T("将选中的项目压缩为 tar.gz"), null, "IconArchive", () => PaneAction(p => p.CompressAsync(true)));
        Add(L10n.T("解压到此处"), L10n.T("解压选中的压缩包"), null, "IconDownload", () => PaneAction(p => p.ExtractAsync()));
        Add(L10n.T("在终端中打开"), L10n.T("在当前位置启动终端"), null, "IconTerminal", () => PaneAction(p => p.OpenTerminalAsync()));
        Add(L10n.T("复制路径"), L10n.T("复制选中项目的完整路径"), null, "IconLink", () => PaneAction(p => p.CopyPathAsync()));
        Add(L10n.T("添加书签"), L10n.T("将当前文件夹加入侧栏"), "Ctrl+D", "IconBookmark", AddBookmarkAsync);
        Add(L10n.T("新建远程连接"), L10n.T("连接到 FTP、SFTP、SMB 或 WebDAV"), null, "IconCloudAdd", () => ShowConnectionDialogAsync(null));
        Add(L10n.T("设置"), L10n.T("打开应用设置"), "Ctrl+,", "IconSettings", OpenSettingsAsync);
        Add(L10n.T("键盘快捷键"), L10n.T("查看所有快捷键"), "F1", "IconKeyboard", () => _services.Dialogs.ShowAboutAsync());
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
        _services.Settings.Changed -= OnSettingsChanged;
        L10n.LanguageChanged -= OnLanguageChanged;
        _services.Operations.Finished -= OnOperationFinished;
        _services.Operations.Operations.CollectionChanged -= OnOperationsChanged;
        _services.FileSystems.Connections.Changed -= OnConnectionsChanged;

        foreach (var tab in Tabs.ToList())
        {
            foreach (var pane in tab.Panes.ToList())
                UnsubscribePane(pane);
            tab.Dispose();
        }
        Tabs.Clear();
    }
}
