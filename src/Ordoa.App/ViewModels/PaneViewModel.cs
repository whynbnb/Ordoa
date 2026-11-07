using System.Collections.ObjectModel;
using Avalonia.Controls;
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

public sealed record BreadcrumbItem(string Label, string Path, bool IsLast);

public sealed partial class PaneViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly List<FileItemViewModel> _entries = [];
    private readonly List<FileItemViewModel> _selected = [];
    private bool _isBatchRenaming;
    private bool _isBatchRenameDialogOpen;
    private bool _isOpenWithDialogOpen;
    private readonly ObservableCollection<FileItemViewModel> _searchResults = [];
    private readonly List<HistoryEntry> _back = [];
    private readonly List<HistoryEntry> _forward = [];
    private IReadOnlyList<TrashEntry> _trashEntries = [];
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _thumbCts;
    private CancellationTokenSource? _gitCts;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _watchDebounce;
    private int _loadVersion;
    private GitRepositoryStatus? _gitStatus;
    private string _lastTitle = string.Empty;
    private bool _lastShowHidden;
    private bool _lastGitEnabled;
    private bool _lastGitBadges;
    private bool _lastGitCleanCheck;
    private bool _lastGitRepositoryBadges;
    private bool _lastGitSubmoduleBadges;
    private bool _lastGitDimIgnored;
    private bool _lastGitStatusBar;
    private string _wideColumnLayout = "*";
    private string? _appliedFolderViewKey;
    private bool _disposed;

    public PaneViewModel(AppServices services, IFileSystem fileSystem, string path)
    {
        _services = services;
        FileSystem = fileSystem;
        _currentPath = fileSystem.NormalizePath(path);
        var settings = services.Settings.Current;
        _sortColumn = settings.SortColumn;
        _sortDescending = settings.SortDescending;
        _foldersFirst = settings.FoldersFirst;
        _viewMode = settings.View;
        _showHidden = settings.ShowHidden;
        _lastShowHidden = settings.ShowHidden;
        _lastGitEnabled = settings.EnableGitIntegration;
        _lastGitBadges = settings.ShowGitFileBadges;
        _lastGitCleanCheck = settings.ShowGitCleanCheck;
        _lastGitRepositoryBadges = settings.ShowGitRepositoryBadges;
        _lastGitSubmoduleBadges = settings.ShowGitSubmoduleBadges;
        _lastGitDimIgnored = settings.DimGitIgnored;
        _lastGitStatusBar = settings.ShowGitStatusBar;
        _wideColumnLayout = BuildColumnLayout(settings);
        _locationText = BuildEditablePath();
        _services.Settings.Changed += OnSettingsChanged;
        _services.Clipboard.Changed += OnClipboardChanged;
    }

    public AppServices Services => _services;
    public IFileSystem FileSystem { get; private set; }
    public string Id { get; } = Guid.NewGuid().ToString("N");

    private string _currentPath = "/";

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (!SetProperty(ref _currentPath, value))
                return;
            OnPropertyChanged(nameof(DisplayPath));
            OnPropertyChanged(nameof(CanGoUp));
            OnPropertyChanged(nameof(LocationText));
            OnPropertyChanged(nameof(IsTrash));
            OnPropertyChanged(nameof(IsRemote));
            OnPropertyChanged(nameof(IsArchive));
            OnPropertyChanged(nameof(FsBadgeText));
            OnPropertyChanged(nameof(SearchScopeText));
        }
    }

    public string DisplayPath => FileSystem.GetDisplayPath(CurrentPath);
    public bool IsRemote => !FileSystem.IsLocal && FileSystem is not ArchiveFileSystem;
    public bool IsTrash => FileSystem is TrashFileSystem;
    public bool IsArchive => FileSystem is ArchiveFileSystem;
    public bool IsLocal => FileSystem.IsLocal;

    private string _title = L10n.T("主目录");

    public string Title
    {
        get => _title;
        private set
        {
            if (SetProperty(ref _title, value))
                TitleChanged?.Invoke(this);
        }
    }

    private string _locationText = string.Empty;
    public string LocationText
    {
        get => _locationText;
        set => SetProperty(ref _locationText, value);
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value))
                return;
            OnPropertyChanged(nameof(HasFilter));
            if (!IsSearchActive)
                ApplySortAndFilter();
        }
    }

    public bool HasFilter => !string.IsNullOrWhiteSpace(FilterText);

    private ViewKind _viewMode;
    public ViewKind ViewMode
    {
        get => _viewMode;
        set
        {
            if (!SetProperty(ref _viewMode, value))
                return;
            OnPropertyChanged(nameof(IsGrid));
            OnPropertyChanged(nameof(ViewIcon));
            OnPropertyChanged(nameof(ViewTooltip));
            SaveFolderViewState();
            if (IsGrid)
                StartThumbnailLoading();
        }
    }

    public string WideColumnLayout
    {
        get => _wideColumnLayout;
        private set => SetProperty(ref _wideColumnLayout, value);
    }

    public bool ShowSizeColumn => _services.Settings.Current.ShowSizeColumn;
    public bool ShowModifiedColumn => _services.Settings.Current.ShowModifiedColumn;
    public bool ShowTypeColumn => _services.Settings.Current.ShowTypeColumn;
    public bool ShowPermissionColumn => _services.Settings.Current.ShowPermissionColumn;
    public bool ShowOwnerColumn => _services.Settings.Current.ShowOwnerColumn;

    private static string BuildColumnLayout(AppSettings settings)
        => $"*,{(settings.ShowSizeColumn ? 74 : 0)},{(settings.ShowModifiedColumn ? 116 : 0)}," +
           $"{(settings.ShowTypeColumn ? 88 : 0)},{(settings.ShowPermissionColumn ? 86 : 0)}," +
           $"{(settings.ShowOwnerColumn ? 92 : 0)}";

    private void RefreshColumnLayout()
    {
        WideColumnLayout = BuildColumnLayout(_services.Settings.Current);
        OnPropertyChanged(nameof(ShowSizeColumn));
        OnPropertyChanged(nameof(ShowModifiedColumn));
        OnPropertyChanged(nameof(ShowTypeColumn));
        OnPropertyChanged(nameof(ShowPermissionColumn));
        OnPropertyChanged(nameof(ShowOwnerColumn));
    }

    private string FolderViewKey =>
        FileSystem.Id + "|" + FileSystem.NormalizePath(CurrentPath);

    private void ApplyFolderViewState()
    {
        var key = FolderViewKey;
        if (string.Equals(key, _appliedFolderViewKey, StringComparison.Ordinal))
            return;
        _appliedFolderViewKey = key;

        if (!_services.Settings.Current.FolderViews.TryGetValue(key, out var state))
            return;

        _sortColumn = state.SortColumn;
        _sortDescending = state.SortDescending;
        _foldersFirst = state.FoldersFirst;
        _viewMode = state.View;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(FoldersFirst));
        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(SortColumnLabel));
        OnPropertyChanged(nameof(IsGrid));
        OnPropertyChanged(nameof(ViewIcon));
        OnPropertyChanged(nameof(ViewTooltip));
    }

    private void SaveFolderViewState()
    {
        var settings = _services.Settings.Current;
        settings.SortColumn = SortColumn;
        settings.SortDescending = SortDescending;
        settings.FoldersFirst = FoldersFirst;
        settings.View = ViewMode;
        settings.FolderViews[FolderViewKey] = new FolderViewState
        {
            SortColumn = SortColumn,
            SortDescending = SortDescending,
            FoldersFirst = FoldersFirst,
            View = ViewMode,
        };
        _services.Settings.Save();
    }

    public bool IsGrid => ViewMode == ViewKind.Grid;
    public Geometry? ViewIcon => FileIconCatalog.Resource(IsGrid ? "IconList" : "IconGrid");
    public string ViewTooltip => IsGrid ? L10n.T("切换为列表视图") : L10n.T("切换为网格视图");

    private SortColumn _sortColumn;
    public SortColumn SortColumn
    {
        get => _sortColumn;
        set
        {
            if (SetProperty(ref _sortColumn, value))
                OnPropertyChanged(nameof(SortColumnLabel));
        }
    }

    private bool _sortDescending;
    public bool SortDescending
    {
        get => _sortDescending;
        set => SetProperty(ref _sortDescending, value);
    }

    private bool _foldersFirst;
    public bool FoldersFirst
    {
        get => _foldersFirst;
        set
        {
            if (!SetProperty(ref _foldersFirst, value))
                return;
            SaveFolderViewState();
            ApplySortAndFilter();
        }
    }

    public string SortColumnLabel => SortColumn switch
    {
        SortColumn.Size => L10n.T("大小"),
        SortColumn.Modified => L10n.T("修改时间"),
        SortColumn.Type => L10n.T("类型"),
        SortColumn.Permissions => L10n.T("权限"),
        SortColumn.Owner => L10n.T("所有者"),
        _ => L10n.T("名称"),
    };

    private bool _showHidden;
    public bool ShowHidden
    {
        get => _showHidden;
        set => SetProperty(ref _showHidden, value);
    }

    private IReadOnlyList<FileItemViewModel> _visibleItems = [];
    public IReadOnlyList<FileItemViewModel> VisibleItems
    {
        get => _visibleItems;
        private set
        {
            if (SetProperty(ref _visibleItems, value))
            {
                OnPropertyChanged(nameof(VisibleCount));
                UpdateEmptyState();
                OnPropertyChanged(nameof(CanSelectAll));
            }
        }
    }

    private IReadOnlyList<BreadcrumbItem> _breadcrumbs = [];
    public IReadOnlyList<BreadcrumbItem> Breadcrumbs
    {
        get => _breadcrumbs;
        private set => SetProperty(ref _breadcrumbs, value);
    }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyHint = L10n.T("此文件夹为空");

    [ObservableProperty]
    private bool _isEditingLocation;

    [ObservableProperty]
    private bool _isSearchActive;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _searchStatus = string.Empty;

    /// <summary>Search from the filesystem root instead of the current folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchScopeText))]
    private bool _searchWholeDisk;

    public string SearchScopeText => SearchWholeDisk
        ? IsLocal ? L10n.T("整个磁盘") : L10n.T("整个连接")
        : L10n.T("当前文件夹");

    public IReadOnlyList<string> SearchHistory => _services.Settings.Current.SearchHistory;
    public IReadOnlyList<SavedSearch> SavedSearches => _services.Settings.Current.SavedSearches;
    public bool HasSearchHistory => SearchHistory.Count > 0;
    public bool HasSavedSearches => SavedSearches.Count > 0;

    [ObservableProperty]
    private bool _isDropTarget;

    [ObservableProperty]
    private string _dropHint = string.Empty;

    [ObservableProperty]
    private string _dropDestination = string.Empty;

    [ObservableProperty]
    private bool _dropShowsModifiers;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>True while this pane shares a tab with a second pane.</summary>
    [ObservableProperty]
    private bool _isInSplit;

    public bool ShowsActiveBar => IsActive && IsInSplit;

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(ShowsActiveBar));

    partial void OnIsInSplitChanged(bool value) => OnPropertyChanged(nameof(ShowsActiveBar));

    [ObservableProperty]
    private string _selectionText = string.Empty;

    [ObservableProperty]
    private string _freeSpaceText = string.Empty;

    [ObservableProperty]
    private double _freeSpaceFraction;

    [ObservableProperty]
    private FileItemViewModel? _primarySelection;

    [ObservableProperty]
    private bool _isOpeningRemote;

    [ObservableProperty]
    private bool _isNarrow;

    public int VisibleCount => VisibleItems.Count;
    public bool HasSelection => _selected.Count > 0;
    public int SelectedCount => _selected.Count;

    public void RefreshLocalization()
    {
        if (KnownFolders.GetDisplayName(CurrentPath) is { } label)
            Title = label;
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(SortColumnLabel));
        OnPropertyChanged(nameof(FsBadgeText));
        OnPropertyChanged(nameof(ViewTooltip));
        UpdateBreadcrumbs();
        UpdateEmptyState();
        UpdateSelectionText();
        foreach (var item in _entries)
            item.RefreshLocalization();
    }

    public string ItemCountText
    {
        get
        {
            if (IsSearchActive)
                return L10n.Format("{0} 个结果", VisibleItems.Count);
            if (HasFilter)
                return L10n.Format("{0} / {1} 项", VisibleItems.Count, _entries.Count);
            return L10n.Format("{0} 项", _entries.Count);
        }
    }

    public string FsBadgeText => FileSystem switch
    {
        TrashFileSystem => L10n.T("回收站"),
        ArchiveFileSystem archive => archive.Format switch
        {
            ArchiveFormat.Zip => "ZIP",
            ArchiveFormat.TarGz => "TAR.GZ",
            _ => "TAR",
        },
        { IsLocal: true } => L10n.T("本机"),
        { Scheme: "sftp" } => "SFTP",
        { Scheme: "ftp" } => "FTP",
        { Scheme: "ftps" } => "FTPS",
        { Scheme: "smb" } => "SMB",
        { Scheme: "webdav" } => "WebDAV",
        _ => L10n.T("远程"),
    };

    public GitRepositoryStatus? GitStatus => _gitStatus;
    public bool HasGitRepository => _gitStatus is not null;
    public bool IsGitDirty => _gitStatus is { IsClean: false };
    public bool CanShowGitStatus => _services.Settings.Current.EnableGitIntegration &&
                                    FileSystem.IsLocal && !IsTrash && !IsArchive;
    public bool ShowGitStatusBar => HasGitRepository && _services.Settings.Current.ShowGitStatusBar;

    public string GitStatusText
    {
        get
        {
            if (_gitStatus is null)
                return string.Empty;
            var text = _gitStatus.BranchLabel;
            if (_gitStatus.Ahead > 0)
                text += $" ↑{_gitStatus.Ahead}";
            if (_gitStatus.Behind > 0)
                text += $" ↓{_gitStatus.Behind}";
            text += _gitStatus.IsClean
                ? " · " + L10n.T("干净")
                : " · " + L10n.Format("{0} 个变更", _gitStatus.ChangedCount);
            return text;
        }
    }

    public IBrush? GitStatusBrush => _gitStatus is null
        ? null
        : _gitStatus.IsClean ? GitBrushes.Added : (IBrush)GitBrushes.Modified;

    public string GitTooltipText
    {
        get
        {
            if (_gitStatus is null)
                return string.Empty;

            var lines = new List<string>
            {
                L10n.Format("仓库: {0}", _gitStatus.Root),
                L10n.Format("分支: {0}", _gitStatus.BranchLabel),
            };
            if (!string.IsNullOrEmpty(_gitStatus.Upstream))
                lines.Add(L10n.Format("上游: {0}", _gitStatus.Upstream));
            if (_gitStatus.Ahead > 0 || _gitStatus.Behind > 0)
                lines.Add(L10n.Format("领先 {0} · 落后 {1}", _gitStatus.Ahead, _gitStatus.Behind));
            lines.Add(L10n.Format("已暂存 {0} · 未暂存 {1} · 未跟踪 {2}",
                _gitStatus.StagedCount, _gitStatus.UnstagedCount, _gitStatus.UntrackedCount));
            if (_gitStatus.ConflictedCount > 0)
                lines.Add(L10n.Format("冲突 {0}", _gitStatus.ConflictedCount));
            return string.Join(Environment.NewLine, lines);
        }
    }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => !FileSystem.IsRoot(CurrentPath) && !IsTrash;

    public bool CanCreateFolder => !IsTrash && FileSystem.Capabilities.Has(FileSystemCapabilities.CreateDirectory);
    public bool CanCreateFile => !IsTrash && FileSystem.Capabilities.Has(FileSystemCapabilities.Write);
    public bool CanPaste => !IsTrash && FileSystem.Capabilities.Has(FileSystemCapabilities.Write);
    public bool CanDelete => HasSelection && FileSystem.Capabilities.Has(FileSystemCapabilities.Delete);
    public bool CanRename => HasSelection && SelectedCount == 1 && !IsTrash && FileSystem.Capabilities.Has(FileSystemCapabilities.Rename);
    public bool CanBatchRename => !_isBatchRenaming && !_isBatchRenameDialogOpen && HasSelection &&
                                  !IsTrash && !IsSearchActive &&
                                  FileSystem.Capabilities.Has(FileSystemCapabilities.Rename);
    public bool CanCopy => HasSelection;
    public bool CanCompress => HasSelection && FileSystem.IsLocal && !IsTrash;
    public bool CanExtract => HasSelection && SelectedCount == 1 && FileSystem.IsLocal && !IsTrash &&
                              PrimarySelection is { IsDirectory: false } p && ArchiveService.IsSupportedArchive(p.Name);
    public bool CanChecksum => HasSelection && _selected.Any(i => !i.IsDirectory);
    public bool CanOpenArchive => HasSelection && SelectedCount == 1 && FileSystem.IsLocal && !IsTrash &&
                                  PrimarySelection is { IsDirectory: false } item &&
                                  ArchiveService.IsSupportedArchive(item.Name);
    public bool CanOpenWith => !_isOpenWithDialogOpen && HasSelection && SelectedCount == 1 && !IsTrash &&
                               PrimarySelection is { IsDirectory: false } && FileSystem.IsLocal &&
                               _services.OpenWith.IsSupported;
    public bool CanOpenInNewWindow => HasSelection && SelectedCount == 1 && !IsTrash &&
                                      FileSystem.IsLocal && _services.Settings.Current.AllowMultipleWindows;
    public bool CanOpenTerminal => FileSystem.IsLocal && !IsTrash;
    public bool CanAnalyze => FileSystem.IsLocal && !IsTrash;
    public string TerminalTargetPath =>
        SelectedCount == 1 && PrimarySelection is { IsDirectory: true } selected
            ? selected.Path
            : CurrentPath;
    public bool CanRunCustomCommands => FileSystem.IsLocal && !IsTrash;
    public bool CanRunCustomCommand(CustomCommand command) =>
        CanRunCustomCommands && (!command.RequiresSelection || HasSelection);
    public IReadOnlyList<TerminalProfile> ContextTerminals => _services.Terminals.GetContextMenuProfiles();
    public TerminalProfile? DefaultTerminal => _services.Terminals.GetDefaultProfile();
    public TerminalProfile? DefaultContextTerminal => _services.Terminals.GetDefaultContextProfile();
    public TerminalProfile? EnabledHerdr => _services.Terminals.GetEnabledHerdr();
    public IReadOnlyList<CustomCommand> ContextCustomCommands => _services.CustomCommands.GetContextMenuCommands();
    public bool CanRestore => IsTrash && HasSelection;
    public bool CanCalculateFolderSize => FileSystem.IsLocal && !IsTrash &&
                                          _selected.Any(item => item.IsDirectory);
    public bool CanEmptyTrash => IsTrash;
    public bool CanSelectAll => VisibleItems.Count > 0;
    public bool CanGoHome => true;

    public event Action<PaneViewModel>? TitleChanged;
    public event Action<PaneViewModel>? Navigated;
    public event Action<PaneViewModel, IFileSystem, string>? OpenInNewTabRequested;
    public event Action<PaneViewModel>? FocusSearchRequested;
    public event Action<PaneViewModel>? FocusLocationRequested;
    public event Action<PaneViewModel>? SelectAllRequested;
    public event Action<PaneViewModel>? OperationStarted;
    public event Action<PaneViewModel>? SelectionChanged;
    public event Action<PaneViewModel>? PreviewRequested;
    public event Action<string, string, bool>? NotificationRequested;

    public async Task InitializeAsync()
    {
        await LoadAsync().ConfigureAwait(true);
    }

    public async Task LoadAsync(bool resetFilter = false)
    {
        if (_disposed)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => LoadAsync(resetFilter)).ConfigureAwait(true);
            return;
        }

        StopWatcher();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _gitCts?.Cancel();
        _gitCts?.Dispose();
        _gitCts = null;
        _loadCts = new CancellationTokenSource();
        var cancellationToken = _loadCts.Token;
        var version = ++_loadVersion;
        ApplyFolderViewState();

        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
        IsEmpty = false;

        try
        {
            if (resetFilter)
                FilterText = string.Empty;

            var entries = await FileSystem.ListAsync(CurrentPath, cancellationToken).ConfigureAwait(true);
            if (version != _loadVersion || cancellationToken.IsCancellationRequested)
                return;

            _trashEntries = IsTrash
                ? await _services.Trash.GetEntriesAsync(cancellationToken).ConfigureAwait(true)
                : [];

            _entries.Clear();
            foreach (var entry in entries)
            {
                if (!ShowHidden && entry.IsHidden)
                    continue;
                _entries.Add(new FileItemViewModel(entry));
            }

            ApplySortAndFilter();
            UpdateBreadcrumbs();
            UpdateTitle();
            UpdateLocationText();
            NotifyNavigationState();
            BumpRecent();
            _ = UpdateFreeSpaceAsync(cancellationToken);
            _ = RefreshGitStatusAsync(version, cancellationToken);

            if (IsGrid)
                StartThumbnailLoading();
            StartWatcher();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (version != _loadVersion)
                return;
            _entries.Clear();
            VisibleItems = [];
            HasError = true;
            ErrorMessage = FriendlyError(ex);
            Title = IsTrash ? L10n.T("回收站") : FileSystem.GetName(CurrentPath);
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                UpdateEmptyState();
                OnPropertyChanged(nameof(ItemCountText));
                Navigated?.Invoke(this);
            }
        }
    }

    public async Task RefreshAsync()
    {
        if (IsSearchActive)
        {
            await StartSearchAsync(FilterText).ConfigureAwait(true);
            return;
        }
        await LoadAsync().ConfigureAwait(true);
    }

    private async Task RefreshGitStatusAsync(int version, CancellationToken cancellationToken)
    {
        if (!CanShowGitStatus)
        {
            ApplyGitStatus(null);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _gitCts = cts;
        try
        {
            var status = await GitService.GetStatusAsync(CurrentPath, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || version != _loadVersion || _disposed)
                return;
            ApplyGitStatus(status);
        }
        catch (OperationCanceledException)
        {
            // Navigation or refresh superseded this request.
        }
        catch (Exception ex)
        {
            Log.Warn("读取 Git 状态失败", ex);
            if (version == _loadVersion)
                ApplyGitStatus(null);
        }
        finally
        {
            if (ReferenceEquals(_gitCts, cts))
                _gitCts = null;
            cts.Dispose();
        }
    }

    private void ApplyGitStatus(GitRepositoryStatus? status)
    {
        _gitStatus = status;
        foreach (var item in _entries)
            ApplyGitItem(item, status);

        OnPropertyChanged(nameof(GitStatus));
        OnPropertyChanged(nameof(HasGitRepository));
        OnPropertyChanged(nameof(IsGitDirty));
        OnPropertyChanged(nameof(ShowGitStatusBar));
        OnPropertyChanged(nameof(GitStatusText));
        OnPropertyChanged(nameof(GitStatusBrush));
        OnPropertyChanged(nameof(GitTooltipText));
    }

    private void ApplyGitItem(FileItemViewModel item, GitRepositoryStatus? status)
    {
        var settings = _services.Settings.Current;
        var enabled = settings.EnableGitIntegration;
        var badges = enabled && settings.ShowGitFileBadges;
        item.SetGitDisplay(badges, settings.ShowGitCleanCheck,
            settings.ShowGitRepositoryBadges, settings.ShowGitSubmoduleBadges,
            settings.DimGitIgnored);

        var itemPath = Path.TrimEndingDirectorySeparator(item.Path);
        var change = status is not null &&
                     status.FilesByPath.TryGetValue(itemPath, out var exact)
            ? exact
            : null;
        var repository = enabled && settings.ShowGitRepositoryBadges &&
                         item.IsDirectory && HasGitMarker(itemPath);
        var ignored = enabled && (status?.IgnoredPaths.Contains(itemPath) == true ||
                      status?.IgnoredDirectories.Any(directory => IsUnder(itemPath, directory)) == true);
        var submodule = enabled && (status?.SubmodulePaths.Contains(itemPath) == true ||
                                    change?.IsSubmodule == true);
        var directoryDirty = enabled && item.IsDirectory && status is not null &&
                             status.ChangedDirectories.Contains(itemPath);
        var trackedClean = enabled && change is null && !ignored && !submodule &&
                           (item.IsDirectory
                               ? status?.TrackedDirectories.Contains(itemPath) == true && !directoryDirty
                               : status?.TrackedPaths.Contains(itemPath) == true);
        item.SetGitState(change, directoryDirty, trackedClean, ignored, repository, submodule);
    }

    /// <summary>
    /// Search results can span repositories. Resolve each repository once and
    /// apply its status to the results that live inside it.
    /// </summary>
    private async Task ApplySearchGitStatusAsync(IReadOnlyList<FileItemViewModel> items,
        CancellationToken cancellationToken)
    {
        var settings = _services.Settings.Current;
        if (!settings.EnableGitIntegration || !settings.ShowGitFileBadges || !FileSystem.IsLocal)
        {
            foreach (var item in items)
            {
                item.SetGitDisplay(false, settings.ShowGitCleanCheck, settings.ShowGitRepositoryBadges,
                    settings.ShowGitSubmoduleBadges, settings.DimGitIgnored);
                item.SetGitState(null);
            }
            return;
        }

        var groups = new Dictionary<string, List<FileItemViewModel>>(
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            var directory = item.IsDirectory ? item.Path : Path.GetDirectoryName(item.Path);
            if (string.IsNullOrEmpty(directory))
                continue;
            var root = GitService.FindRepository(directory);
            if (root is null)
                continue;
            if (!groups.TryGetValue(root, out var list))
                groups[root] = list = [];
            list.Add(item);
        }

        foreach (var (root, group) in groups.Take(16))
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            GitRepositoryStatus? status;
            try
            {
                status = await GitService.GetStatusForRootAsync(root, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("读取搜索结果 Git 状态失败", ex);
                continue;
            }

            foreach (var item in group)
                ApplyGitItem(item, status);
        }
    }

    private void RememberSearch(string query)
    {
        var history = _services.Settings.Current.SearchHistory;
        history.RemoveAll(entry => string.Equals(entry, query, StringComparison.Ordinal));
        history.Insert(0, query);
        while (history.Count > 20)
            history.RemoveAt(history.Count - 1);
        _services.Settings.Save();
        OnPropertyChanged(nameof(SearchHistory));
        OnPropertyChanged(nameof(HasSearchHistory));
    }

    public async Task SaveCurrentSearchAsync()
    {
        var query = FilterText.Trim();
        if (query.Length == 0)
        {
            Toast(L10n.T("无法保存搜索"), L10n.T("请先输入搜索内容"), true);
            return;
        }

        var name = await _services.Dialogs.PromptAsync(L10n.T("保存搜索"), L10n.T("名称"), query)
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var searches = _services.Settings.Current.SavedSearches;
        searches.RemoveAll(entry => string.Equals(entry.Name, name.Trim(), StringComparison.Ordinal));
        searches.Insert(0, new SavedSearch { Name = name.Trim(), Query = query });
        while (searches.Count > 50)
            searches.RemoveAt(searches.Count - 1);
        _services.Settings.Save();
        OnPropertyChanged(nameof(SavedSearches));
        OnPropertyChanged(nameof(HasSavedSearches));
        Toast(L10n.T("已保存搜索"), name.Trim(), false);
    }

    public void RemoveSavedSearch(SavedSearch search)
    {
        var searches = _services.Settings.Current.SavedSearches;
        if (!searches.Remove(search))
            return;
        _services.Settings.Save();
        OnPropertyChanged(nameof(SavedSearches));
        OnPropertyChanged(nameof(HasSavedSearches));
    }

    public void ClearSearchHistory()
    {
        var history = _services.Settings.Current.SearchHistory;
        if (history.Count == 0)
            return;
        history.Clear();
        _services.Settings.Save();
        OnPropertyChanged(nameof(SearchHistory));
        OnPropertyChanged(nameof(HasSearchHistory));
    }

    public async Task RunSearchAsync(string query)
    {
        FilterText = query;
        await StartSearchAsync(query).ConfigureAwait(true);
    }

    public async Task ToggleSearchScopeAsync()
    {
        SearchWholeDisk = !SearchWholeDisk;
        if (IsSearchActive)
            await StartSearchAsync(FilterText).ConfigureAwait(true);
    }

    private static bool HasGitMarker(string path)
        => Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    private static bool IsUnder(string path, string directory)
        => path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (_back.Count == 0)
            return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(new HistoryEntry(FileSystem, CurrentPath));
        await NavigateCoreAsync(target.FileSystem, target.Path).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoForwardAsync()
    {
        if (_forward.Count == 0)
            return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(new HistoryEntry(FileSystem, CurrentPath));
        await NavigateCoreAsync(target.FileSystem, target.Path).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (!CanGoUp)
            return;
        var parent = FileSystem.GetParent(CurrentPath);
        await NavigateAsync(FileSystem, parent).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    [RelayCommand]
    private void ToggleView() => ViewMode = ViewMode == ViewKind.List ? ViewKind.Grid : ViewKind.List;

    [RelayCommand]
    private async Task SortByAsync(string? columnName)
    {
        if (!Enum.TryParse<SortColumn>(columnName, true, out var column))
            return;

        if (SortColumn == column)
            SortDescending = !SortDescending;
        else
        {
            SortColumn = column;
            SortDescending = false;
        }

        SaveFolderViewState();
        ApplySortAndFilter();
        if (IsSearchActive && _searchResults.Count > 0)
            SortSearchResults();
        await Task.CompletedTask;
    }

    public async Task NavigateAsync(IFileSystem fileSystem, string path, bool addHistory = true)
    {
        path = fileSystem.NormalizePath(path);
        if (fileSystem.Id == FileSystem.Id && string.Equals(path, CurrentPath, StringComparison.Ordinal))
        {
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        if (addHistory)
        {
            _back.Add(new HistoryEntry(FileSystem, CurrentPath));
            _forward.Clear();
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }

        await NavigateCoreAsync(fileSystem, path).ConfigureAwait(true);
    }

    private async Task NavigateCoreAsync(IFileSystem fileSystem, string path)
    {
        CancelSearch();
        StopWatcher();
        FileSystem = fileSystem;
        CurrentPath = fileSystem.NormalizePath(path);
        FilterText = string.Empty;
        IsSearchActive = false;
        _searchResults.Clear();
        IsEditingLocation = false;
        IsDropTarget = false;
        PrimarySelection = null;
        _selected.Clear();
        UpdateSelectionDependentProperties();
        OnPropertyChanged(nameof(IsTrash));
        OnPropertyChanged(nameof(IsRemote));
            OnPropertyChanged(nameof(IsArchive));
        OnPropertyChanged(nameof(FsBadgeText));
        OnPropertyChanged(nameof(CanCreateFolder));
        OnPropertyChanged(nameof(CanCreateFile));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanBatchRename));
        OnPropertyChanged(nameof(CanCompress));
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(CanChecksum));
        OnPropertyChanged(nameof(CanOpenArchive));
        OnPropertyChanged(nameof(CanShowGitStatus));
        OnPropertyChanged(nameof(CanOpenWith));
        OnPropertyChanged(nameof(CanOpenInNewWindow));
        OnPropertyChanged(nameof(CanOpenTerminal));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CanEmptyTrash));
        OnPropertyChanged(nameof(CanGoUp));
        UpdateBreadcrumbs();
        UpdateLocationText();
        await LoadAsync(true).ConfigureAwait(true);
        NotifyNavigationState();
    }

    public async Task NavigateToBreadcrumbAsync(BreadcrumbItem item)
        => await NavigateAsync(FileSystem, item.Path).ConfigureAwait(true);

    public async Task OpenPathAsync(string path)
        => await NavigateAsync(FileSystem, path).ConfigureAwait(true);

    public Task OpenEntryAsync(FileItemViewModel item) => OpenAsync(item);

    public async Task OpenAsync(FileItemViewModel? item = null)
    {
        item ??= PrimarySelection;
        if (item is null)
            return;

        if (IsTrash)
        {
            await RestoreAndOpenAsync(item).ConfigureAwait(true);
            return;
        }

        if (item.IsDirectory)
        {
            await NavigateAsync(FileSystem, item.Path).ConfigureAwait(true);
            return;
        }

        if (FileSystem.IsLocal && ArchiveService.IsSupportedArchive(item.Name))
        {
            await OpenArchiveAsync(item).ConfigureAwait(true);
            return;
        }

        if (FileSystem.IsLocal)
        {
            try
            {
                if (!_services.OpenWith.TryOpenDefault(item.Path))
                    await _services.Shell.OpenAsync(item.Path).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Toast(L10n.T("无法打开文件"), ex.Message, true);
            }
            return;
        }

        await OpenRemoteFileAsync(item).ConfigureAwait(true);
    }

    public async Task OpenArchiveAsync(FileItemViewModel? item = null)
    {
        item ??= PrimarySelection;
        if (item is null || item.IsDirectory || !FileSystem.IsLocal ||
            !ArchiveService.IsSupportedArchive(item.Name) || IsTrash)
            return;

        try
        {
            IsLoading = true;
            var archive = await ArchiveFileSystem.OpenAsync(item.Path).ConfigureAwait(true);
            await NavigateAsync(archive, "/").ConfigureAwait(true);
            Toast(L10n.T("已打开压缩包"), item.Name, false);
        }
        catch (Exception ex)
        {
            Toast(L10n.T("无法打开压缩包"), ex.Message, true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task OpenWithAsync()
    {
        if (!CanOpenWith || PrimarySelection is not { } item)
            return;

        _isOpenWithDialogOpen = true;
        OnPropertyChanged(nameof(CanOpenWith));
        try
        {
            var applications = _services.OpenWith.GetApplications(item.Entry);
            if (applications.Count == 0)
            {
                await OpenAsync(item).ConfigureAwait(true);
                return;
            }

            var choice = await _services.Dialogs
                .ShowOpenWithAsync(item.Entry, applications, _services.OpenWith.GetDefault(item.Extension))
                .ConfigureAwait(true);
            if (choice is null)
                return;

            if (choice.DesktopId is null)
            {
                if (choice.Remember)
                    _services.OpenWith.ClearDefault(item.Extension);
                await OpenSystemDefaultAsync(item).ConfigureAwait(true);
                return;
            }

            if (choice.Remember)
                _services.OpenWith.Remember(item.Extension, choice.DesktopId);

            var application = _services.OpenWith.Find(choice.DesktopId);
            if (application is null || !_services.OpenWith.Open(item.Path, application))
            {
                Toast(L10n.T("无法打开文件"),
                    L10n.T("所选应用不可用，已回退到系统默认应用"), true);
                await OpenSystemDefaultAsync(item).ConfigureAwait(true);
            }
        }
        finally
        {
            _isOpenWithDialogOpen = false;
            OnPropertyChanged(nameof(CanOpenWith));
        }
    }

    private async Task OpenSystemDefaultAsync(FileItemViewModel item)
    {
        try
        {
            await _services.Shell.OpenAsync(item.Path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Toast(L10n.T("无法打开文件"), ex.Message, true);
        }
    }

    public async Task OpenSelectionInNewTabAsync(FileItemViewModel? item = null)
    {
        item ??= PrimarySelection;
        if (item is null)
            return;

        if (IsTrash)
        {
            await RestoreAndOpenAsync(item).ConfigureAwait(true);
            return;
        }

        if (item.IsDirectory)
            OpenInNewTabRequested?.Invoke(this, FileSystem, item.Path);
        else
            OpenInNewTabRequested?.Invoke(this, FileSystem, FileSystem.GetParent(item.Path));
    }

    public async Task OpenInNewWindowAsync()
    {
        if (!CanOpenInNewWindow || PrimarySelection is not { } item)
            return;

        var path = item.IsDirectory ? item.Path : FileSystem.GetParent(item.Path);
        IReadOnlyList<string>? selection = item.IsDirectory ? null : [item.Path];
        await _services.Windows.CreateWindowAsync(path, restoreSession: false, selection).ConfigureAwait(true);
    }

    public void RequestSearchFocus() => FocusSearchRequested?.Invoke(this);
    public void RequestPreview() => PreviewRequested?.Invoke(this);
    public void RequestLocationFocus() => FocusLocationRequested?.Invoke(this);
    public void RequestSelectAll() => SelectAllRequested?.Invoke(this);

    public void BeginEditLocation()
    {
        IsEditingLocation = true;
        LocationText = BuildEditablePath();
        FocusLocationRequested?.Invoke(this);
    }

    public void CancelEditLocation()
    {
        IsEditingLocation = false;
        UpdateLocationText();
    }

    public async Task CommitLocationAsync()
    {
        var text = LocationText.Trim();
        IsEditingLocation = false;
        if (string.IsNullOrEmpty(text))
        {
            UpdateLocationText();
            return;
        }

        try
        {
            await ResolveLocationAsync(text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Toast(L10n.T("无法打开位置"), ex.Message, true);
            UpdateLocationText();
        }
    }

    public async Task ResolveLocationAsync(string text)
    {
        text = text.Trim();

        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(text, UriKind.Absolute, out var fileUri))
        {
            await NavigateAsync(_services.FileSystems.Local, fileUri.LocalPath).ConfigureAwait(true);
            return;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 &&
            uri.Scheme is not "file")
        {
            await OpenRemoteUriAsync(uri).ConfigureAwait(true);
            return;
        }

        if (FileSystem.IsLocal)
        {
            var localPath = LocalFileSystem.ExpandUser(text);
            if (!Path.IsPathRooted(localPath))
                localPath = Path.Combine(CurrentPath, localPath);
            localPath = _services.FileSystems.Local.NormalizePath(localPath);

            var entry = await _services.FileSystems.Local.GetEntryAsync(localPath).ConfigureAwait(true);
            if (entry is null)
                throw new DirectoryNotFoundException(L10n.Format("路径不存在: {0}", localPath));
            if (!entry.IsDirectory)
            {
                await NavigateAsync(_services.FileSystems.Local, _services.FileSystems.Local.GetParent(localPath)).ConfigureAwait(true);
                return;
            }
            await NavigateAsync(_services.FileSystems.Local, localPath).ConfigureAwait(true);
            return;
        }

        var remotePath = text.StartsWith('/') ? FileSystem.NormalizePath(text) : FileSystem.Combine(CurrentPath, text);
        await NavigateAsync(FileSystem, remotePath).ConfigureAwait(true);
    }

    private async Task OpenRemoteUriAsync(Uri uri)
    {
        var scheme = uri.Scheme.ToLowerInvariant();
        var protocol = scheme switch
        {
            "ftp" => RemoteProtocol.Ftp,
            "ftps" => RemoteProtocol.Ftps,
            "sftp" => RemoteProtocol.Sftp,
            "smb" => RemoteProtocol.Smb,
            "webdav" or "dav" or "http" or "https" => RemoteProtocol.WebDav,
            _ => throw new NotSupportedException(L10n.Format("不支持的位置类型: {0}", scheme)),
        };

        var userInfo = uri.UserInfo.Split(':', 2);
        var user = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : string.Empty;
        string? uriPassword = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null;
        var host = uri.Host;
        var port = uri.IsDefaultPort ? 0 : uri.Port;
        var saved = _services.FileSystems.Connections.GetAll().FirstOrDefault(p =>
            p.Protocol == protocol &&
            string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase) &&
            (port == 0 || p.EffectivePort == port) &&
            (string.IsNullOrEmpty(user) || string.Equals(p.Username, user, StringComparison.Ordinal)));
        if (saved is null)
            saved = _services.FileSystems.Connections.GetAll().FirstOrDefault(p =>
                p.Protocol == protocol && string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase));

        ConnectionProfile? profile = saved;
        string? password = null;
        if (profile is null)
        {
            profile = new ConnectionProfile
            {
                Protocol = protocol,
                Host = host,
                Port = port,
                Username = user,
                InitialPath = "/",
                Secure = scheme is "https" or "webdav" or "dav" || protocol != RemoteProtocol.WebDav,
            };
            var result = await _services.Dialogs.ShowConnectionAsync(profile).ConfigureAwait(true);
            if (result is null)
                return;
            profile = result.Profile;
            password = result.Password;
            if (result.SaveProfile)
                await _services.FileSystems.SaveProfileAsync(profile, password).ConfigureAwait(true);
        }

        IFileSystem fs;
        try
        {
            fs = await _services.FileSystems.ConnectAsync(profile, password ?? uriPassword).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var stored = _services.FileSystems.Connections.Get(profile.Id);
            if (stored is not null)
            {
                stored.LastError = ex.Message;
                _services.FileSystems.Connections.Save(stored);
            }
            throw;
        }

        if (profile.LastError is not null)
        {
            profile.LastError = null;
            if (_services.FileSystems.Connections.Get(profile.Id) is not null)
                _services.FileSystems.Connections.Save(profile);
        }

        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? fs.RootPath : Uri.UnescapeDataString(uri.AbsolutePath);
        await NavigateAsync(fs, path).ConfigureAwait(true);
    }

    public void UpdateSelection(IEnumerable<FileItemViewModel> items)
    {
        _selected.Clear();
        _selected.AddRange(items);
        PrimarySelection = _selected.FirstOrDefault();
        UpdateSelectionDependentProperties();
    }

    public void ClearSelection()
    {
        _selected.Clear();
        PrimarySelection = null;
        UpdateSelectionDependentProperties();
    }

    /// <summary>Selects items by absolute path, used when another app asks Ordoa to reveal files.</summary>
    public void SelectPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        var comparer = FileSystem.IsCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var wanted = paths
            .Select(path => FileSystem.NormalizePath(path))
            .ToHashSet(comparer);
        var items = VisibleItems
            .Where(item => wanted.Contains(FileSystem.NormalizePath(item.Path)))
            .ToList();

        if (items.Count > 0)
            UpdateSelection(items);
    }

    private void UpdateSelectionDependentProperties()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanBatchRename));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanCompress));
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(CanChecksum));
        OnPropertyChanged(nameof(CanOpenArchive));
        OnPropertyChanged(nameof(CanShowGitStatus));
        OnPropertyChanged(nameof(CanOpenWith));
        OnPropertyChanged(nameof(CanOpenInNewWindow));
        OnPropertyChanged(nameof(CanRestore));
        UpdateSelectionText();
        SelectionChanged?.Invoke(this);
    }

    private void UpdateSelectionText()
    {
        if (_selected.Count == 0)
        {
            SelectionText = string.Empty;
            return;
        }

        if (_selected.Count == 1)
        {
            var item = _selected[0];
            SelectionText = item.IsDirectory ? item.Name : $"{item.Name} · {Humanize.Size(item.Entry.Size)}";
            return;
        }

        var bytes = _selected.Where(i => !i.IsDirectory).Sum(i => i.Entry.Size);
        SelectionText = L10n.Format("{0} 项 · {1}", _selected.Count, Humanize.Size(bytes));
    }

    public async Task CopySelectionAsync(bool cut)
    {
        if (!HasSelection)
            return;
        var sources = _selected.Select(i => new OperationSource(FileSystem, i.Entry)).ToList();
        await _services.Clipboard.SetInternalAsync(sources, cut).ConfigureAwait(true);
        Toast(cut ? L10n.T("已剪切") : L10n.T("已复制"), L10n.Format("{0} 个项目", sources.Count), false);
    }

    public async Task PasteAsync()
    {
        if (!CanPaste)
            return;

        ClipboardPayload? payload = null;
        try
        {
            payload = await _services.Clipboard.TryReadInternalAsync().ConfigureAwait(true);
        }
        catch { /* ignore */ }

        if (payload is { Sources.Count: > 0 })
        {
            var preview = await PreviewTransferAsync(payload.Sources, FileSystem, CurrentPath, payload.IsCut)
                .ConfigureAwait(true);
            if (preview == ConflictChoice.Cancel)
                return;
            _services.Operations.Copy(payload.Sources, FileSystem, CurrentPath, payload.IsCut, preview);
            if (payload.IsCut)
                await _services.Clipboard.ConsumeCutAsync().ConfigureAwait(true);
            OperationStarted?.Invoke(this);
            Toast(payload.IsCut ? L10n.T("正在移动") : L10n.T("正在复制"), L10n.Format("{0} 个项目", payload.Sources.Count), false);
            return;
        }

        var external = await _services.Clipboard.TryReadExternalAsync().ConfigureAwait(true);
        if (external.Count > 0)
        {
            var preview = await PreviewTransferAsync(external, FileSystem, CurrentPath, false).ConfigureAwait(true);
            if (preview == ConflictChoice.Cancel)
                return;
            _services.Operations.Copy(external, FileSystem, CurrentPath, false, preview);
            OperationStarted?.Invoke(this);
            Toast(L10n.T("正在复制"), L10n.Format("{0} 个项目", external.Count), false);
            return;
        }

        Toast(L10n.T("没有可粘贴的项目"), L10n.T("剪贴板为空或内容不是文件"), false);
    }

    public async Task DeleteSelectionAsync(bool permanent)
    {
        if (!HasSelection)
            return;

        var sources = _selected.Select(i => new OperationSource(FileSystem, i.Entry)).ToList();
        var forcePermanent = permanent || IsTrash || !FileSystem.IsLocal || !_services.Trash.IsSupported;

        if (forcePermanent && _services.Settings.Current.ConfirmPermanentDelete)
        {
            var ok = await _services.Dialogs.ConfirmAsync(
                IsTrash ? L10n.T("从回收站永久删除？") : L10n.T("永久删除这些项目？"),
                BuildDeleteMessage(sources),
                permanent ? L10n.T("永久删除") : L10n.T("删除"),
                true).ConfigureAwait(true);
            if (!ok)
                return;
        }

        _services.Operations.Delete(sources, forcePermanent);
        OperationStarted?.Invoke(this);
        Toast(forcePermanent ? L10n.T("正在删除") : L10n.T("正在移到回收站"), L10n.Format("{0} 个项目", sources.Count), false);
    }

    public async Task RestoreSelectionAsync()
    {
        if (!IsTrash || !HasSelection)
            return;

        var entries = _selected
            .Select(item => _trashEntries.FirstOrDefault(e => string.Equals(e.TrashPath, item.Path, StringComparison.Ordinal)))
            .Where(e => e is not null)
            .Cast<TrashEntry>()
            .ToList();
        if (entries.Count == 0)
            return;

        _services.Operations.Restore(entries);
        OperationStarted?.Invoke(this);
        Toast(L10n.T("正在恢复"), L10n.Format("{0} 个项目", entries.Count), false);
    }

    public async Task EmptyTrashAsync()
    {
        if (!IsTrash)
            return;

        var ok = await _services.Dialogs.ConfirmAsync(L10n.T("清空回收站？"), L10n.T("回收站中的所有项目将被永久删除，此操作无法撤销。"),
            L10n.T("清空"), true).ConfigureAwait(true);
        if (!ok)
            return;

        _services.Operations.EmptyTrash();
        OperationStarted?.Invoke(this);
        Toast(L10n.T("正在清空回收站"), string.Empty, false);
    }

    public async Task RenameAsync()
    {
        if (!CanRename || PrimarySelection is null)
            return;

        var item = PrimarySelection;
        var name = await _services.Dialogs.PromptAsync(L10n.T("重命名"), L10n.T("新名称"), item.Name,
            value => ValidateName(value, item.Name)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, item.Name, StringComparison.Ordinal))
            return;

        name = name.Trim();
        var parent = FileSystem.GetParent(item.Path);
        var target = FileSystem.Combine(parent, name);
        if (!string.Equals(FileSystem.NormalizePath(target), FileSystem.NormalizePath(item.Path), StringComparison.Ordinal) &&
            await FileSystem.ExistsAsync(target).ConfigureAwait(true))
        {
            Toast(L10n.T("无法重命名"), L10n.T("同名项目已存在"), true);
            return;
        }

        try
        {
            await FileSystem.MoveAsync(item.Path, target).ConfigureAwait(true);
            var renamedEntry = await FileSystem.GetEntryAsync(target).ConfigureAwait(true) ?? item.Entry;
            _services.Undo.RecordRename(FileSystem, item.Path, target, renamedEntry,
                L10n.Format("重命名 {0}", item.Name));
            await RefreshAsync().ConfigureAwait(true);
            Toast(L10n.T("已重命名"), $"{item.Name} → {name}", false);
        }
        catch (Exception ex)
        {
            Toast(L10n.T("重命名失败"), ex.Message, true);
        }
    }

    public async Task BatchRenameAsync()
    {
        if (!CanBatchRename)
            return;

        _isBatchRenameDialogOpen = true;
        OnPropertyChanged(nameof(CanBatchRename));
        try
        {
            var selected = _selected.ToHashSet();
            var entries = _entries.Where(i => selected.Contains(i)).Select(i => i.Entry).ToList();
            if (entries.Count != _selected.Count)
                entries = _selected.Select(i => i.Entry).ToList();
            if (entries.Count == 0)
                return;

            var parent = FileSystem.NormalizePath(CurrentPath);
            var pathComparison = FileSystem.IsCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (entries.Any(entry => !string.Equals(
                    FileSystem.NormalizePath(FileSystem.GetParent(entry.Path)),
                    parent,
                    pathComparison)))
            {
                Toast(L10n.T("无法批量重命名"), L10n.T("批量重命名只支持同一文件夹中的项目"), true);
                return;
            }

            IReadOnlyList<string> existingNames;
            try
            {
                var listing = await FileSystem.ListAsync(CurrentPath).ConfigureAwait(true);
                existingNames = listing.Select(e => e.Name).ToList();
            }
            catch
            {
                existingNames = _entries.Select(i => i.Entry.Name).ToList();
            }

            var request = new BatchRenameRequest(
                entries,
                existingNames,
                FileSystem.IsCaseSensitive,
                FileNameRules.Detect(FileSystem));

            var plan = await _services.Dialogs.ShowBatchRenameAsync(request).ConfigureAwait(true);
            if (plan is null)
                return;

            _isBatchRenaming = true;
            OnPropertyChanged(nameof(CanBatchRename));
            try
            {
                var result = await BatchRenameExecutor.ExecuteAsync(FileSystem, plan).ConfigureAwait(true);
                if (result.Completed.Count > 0)
                {
                    _services.Undo.RecordBatchRename(FileSystem, result.Completed,
                        L10n.Format("批量重命名 {0} 个项目", result.Completed.Count));
                }

                await RefreshAsync().ConfigureAwait(true);

                if (result.Errors.Count == 0)
                {
                    Toast(L10n.T("已重命名"), L10n.Format("{0} 个项目", result.Renamed), false);
                    return;
                }

                Toast(L10n.T("批量重命名未完成"),
                    L10n.Format("{0} 个成功，{1} 个失败", result.Renamed, result.Failed), true);

                var details = string.Join(Environment.NewLine, result.Errors.Take(8));
                if (result.Errors.Count > 8)
                    details += Environment.NewLine + L10n.Format("还有 {0} 个错误", result.Errors.Count - 8);
                await _services.Dialogs.ShowErrorAsync(L10n.T("批量重命名错误"), details).ConfigureAwait(true);
            }
            finally
            {
                _isBatchRenaming = false;
                OnPropertyChanged(nameof(CanBatchRename));
            }
        }
        finally
        {
            _isBatchRenameDialogOpen = false;
            OnPropertyChanged(nameof(CanBatchRename));
        }
    }

    public async Task NewFolderAsync()
    {
        if (!CanCreateFolder)
            return;

        var name = await _services.Dialogs.PromptAsync(L10n.T("新建文件夹"), L10n.T("文件夹名称"), L10n.T("新建文件夹"),
            value => ValidateNewName(value)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var target = FileSystem.Combine(CurrentPath, name.Trim());
        try
        {
            if (await FileSystem.ExistsAsync(target).ConfigureAwait(true))
            {
                Toast(L10n.T("无法创建"), L10n.T("同名项目已存在"), true);
                return;
            }
            await FileSystem.CreateDirectoryAsync(target).ConfigureAwait(true);
            var created = await FileSystem.GetEntryAsync(target).ConfigureAwait(true);
            if (created is not null)
                _services.Undo.RecordNew(FileSystem, target, created, L10n.Format("新建文件夹 {0}", name.Trim()));
            await RefreshAsync().ConfigureAwait(true);
            Toast(L10n.T("已创建文件夹"), name.Trim(), false);
        }
        catch (Exception ex)
        {
            Toast(L10n.T("创建文件夹失败"), ex.Message, true);
        }
    }

    public async Task NewFileAsync()
    {
        if (!CanCreateFile)
            return;

        var name = await _services.Dialogs.PromptAsync(L10n.T("新建空文件"), L10n.T("文件名称"), L10n.T("未命名.txt"),
            value => ValidateNewName(value)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var target = FileSystem.Combine(CurrentPath, name.Trim());
        try
        {
            if (await FileSystem.ExistsAsync(target).ConfigureAwait(true))
            {
                Toast(L10n.T("无法创建"), L10n.T("同名项目已存在"), true);
                return;
            }
            await using (await FileSystem.OpenWriteAsync(target, false).ConfigureAwait(true)) { }
            var created = await FileSystem.GetEntryAsync(target).ConfigureAwait(true);
            if (created is not null)
                _services.Undo.RecordNew(FileSystem, target, created, L10n.Format("新建文件 {0}", name.Trim()));
            await RefreshAsync().ConfigureAwait(true);
            Toast(L10n.T("已创建文件"), name.Trim(), false);
        }
        catch (Exception ex)
        {
            Toast(L10n.T("创建文件失败"), ex.Message, true);
        }
    }

    public async Task PropertiesAsync()
    {
        var entry = PrimarySelection?.Entry ?? await FileSystem.GetEntryAsync(CurrentPath).ConfigureAwait(true);
        if (entry is null)
            return;
        await _services.Dialogs.ShowPropertiesAsync(entry, FileSystem).ConfigureAwait(true);
    }

    public async Task ChecksumAsync()
    {
        var sources = _selected.Where(i => !i.IsDirectory)
            .Select(i => new OperationSource(FileSystem, i.Entry))
            .ToList();
        if (sources.Count == 0 && PrimarySelection is { IsDirectory: false } primary)
            sources.Add(new OperationSource(FileSystem, primary.Entry));
        if (sources.Count == 0)
        {
            Toast(L10n.T("无法计算校验和"), L10n.T("请选择文件"), false);
            return;
        }

        await _services.Dialogs.ShowChecksumAsync(sources).ConfigureAwait(true);
    }

    public async Task CompressAsync(bool tarGz)
    {
        if (!CanCompress)
            return;

        var paths = _selected.Where(i => i.Entry.Path.Length > 0).Select(i => i.Entry.Path).ToList();
        if (paths.Count == 0)
            return;

        var extension = tarGz ? ".tar.gz" : ".zip";
        var baseName = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar)) : L10n.T("压缩包");
        var candidateName = baseName + extension;
        if (!IsNameFree(candidateName))
            candidateName = GlobMatcher.SuggestFileName(candidateName, IsNameTaken);
        var destination = Path.Combine(CurrentPath, candidateName);

        Toast(L10n.T("正在压缩"), candidateName, false);
        try
        {
            await ArchiveService.CreateAsync(paths, destination).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            Toast(L10n.T("压缩完成"), candidateName, false);
        }
        catch (Exception ex)
        {
            Toast(L10n.T("压缩失败"), ex.Message, true);
        }
    }

    public async Task ExtractAsync()
    {
        if (!CanExtract || PrimarySelection is null)
            return;

        var archive = PrimarySelection;
        Toast(L10n.T("正在解压"), archive.Name, false);
        try
        {
            var destination = await ArchiveService.ExtractAsync(archive.Path, CurrentPath).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            Toast(L10n.T("解压完成"), Path.GetFileName(destination), false);
        }
        catch (Exception ex)
        {
            Toast(L10n.T("解压失败"), ex.Message, true);
        }
    }

    public async Task CopyPathAsync()
    {
        var text = PrimarySelection is { } item ? FileSystem.GetDisplayPath(item.Path) : DisplayPath;
        await _services.Clipboard.SetTextAsync(text).ConfigureAwait(true);
        Toast(L10n.T("已复制路径"), text, false);
    }

    public async Task CopyNameAsync()
    {
        var text = PrimarySelection?.Name;
        if (string.IsNullOrEmpty(text))
            return;
        await _services.Clipboard.SetTextAsync(text).ConfigureAwait(true);
        Toast(L10n.T("已复制名称"), text, false);
    }

    public Task OpenTerminalAsync() => OpenTerminalAsync(null);

    public Task OpenTerminalAsync(string? terminalId)
    {
        if (!CanOpenTerminal)
            return Task.CompletedTask;

        var path = TerminalTargetPath;
        var success = string.IsNullOrWhiteSpace(terminalId)
            ? _services.Terminals.OpenDefault(path)
            : _services.Terminals.OpenById(terminalId, path);
        if (!success)
            Toast(L10n.T("无法打开终端"), L10n.T("未找到可用的终端程序"), true);
        return Task.CompletedTask;
    }

    public Task OpenInHerdrAsync()
    {
        if (!CanOpenTerminal)
            return Task.CompletedTask;

        var success = _services.Terminals.OpenInHerdr(TerminalTargetPath);
        if (!success)
            Toast(L10n.T("无法打开 Herdr"), L10n.T("请确认已安装 herdr，并且当前终端支持运行命令"), true);
        return Task.CompletedTask;
    }

    public async Task AnalyzeDiskUsageAsync()
    {
        if (!CanAnalyze)
        {
            Toast(L10n.T("无法分析"), L10n.T("仅支持本地文件夹"), true);
            return;
        }
        await _services.Dialogs.ShowDiskUsageAsync(TerminalTargetPath).ConfigureAwait(true);
    }

    public async Task FindDuplicateFilesAsync()
    {
        if (!CanAnalyze)
        {
            Toast(L10n.T("无法分析"), L10n.T("仅支持本地文件夹"), true);
            return;
        }
        await _services.Dialogs.ShowDuplicateFinderAsync(TerminalTargetPath).ConfigureAwait(true);
    }

    public async Task ShowGitStatusAsync()
    {
        if (!CanShowGitStatus)
        {
            Toast(L10n.T("无法查看 Git 状态"), L10n.T("仅支持本地文件夹"), true);
            return;
        }

        var path = await _services.Dialogs.ShowGitStatusAsync(TerminalTargetPath).ConfigureAwait(true);
        if (string.IsNullOrEmpty(path))
            return;

        var parent = FileSystem.GetParent(path);
        await NavigateAsync(_services.FileSystems.Local, parent).ConfigureAwait(true);
        SelectPaths([path]);
    }

    public Task RunCustomCommandAsync(string id)
    {
        var command = _services.CustomCommands.Find(id);
        if (command is null)
            return Task.CompletedTask;
        if (!CanRunCustomCommands)
        {
            Toast(L10n.T("无法运行命令"), L10n.T("自定义命令只支持本地文件夹"), true);
            return Task.CompletedTask;
        }
        if (!CanRunCustomCommand(command))
        {
            Toast(L10n.T("无法运行命令"), L10n.T("该命令需要先选中文件或文件夹"), true);
            return Task.CompletedTask;
        }

        var paths = _selected.Select(item => item.Entry.Path).ToList();
        var success = _services.CustomCommands.Run(command, CurrentPath, paths);
        if (!success)
            Toast(L10n.T("无法运行命令"), command.Name, true);
        return Task.CompletedTask;
    }

    public void ToggleSelection(FileItemViewModel item, bool selected)
    {
        if (selected)
        {
            if (!_selected.Contains(item))
                _selected.Add(item);
        }
        else
        {
            _selected.Remove(item);
        }

        PrimarySelection = _selected.FirstOrDefault();
        UpdateSelectionDependentProperties();
    }

    public async Task CalculateFolderSizesAsync()
    {
        var directories = _selected.Where(item => item.IsDirectory).ToList();
        if (directories.Count == 0)
        {
            Toast(L10n.T("没有选中文件夹"), L10n.T("请选择一个或多个文件夹"), false);
            return;
        }

        foreach (var item in directories)
            item.IsCalculatingFolderSize = true;

        try
        {
            await Task.WhenAll(directories.Select(async item =>
            {
                try
                {
                    var bytes = await MeasureDirectoryAsync(item.Path, CancellationToken.None)
                        .ConfigureAwait(true);
                    item.FolderSizeText = Humanize.Size(bytes);
                }
                catch (Exception ex)
                {
                    Log.Warn($"计算文件夹大小失败: {item.Path}", ex);
                    item.FolderSizeText = L10n.T("无法计算");
                }
                finally
                {
                    item.IsCalculatingFolderSize = false;
                }
            })).ConfigureAwait(true);
        }
        finally
        {
            foreach (var item in directories)
                item.IsCalculatingFolderSize = false;
        }
    }

    private static Task<long> MeasureDirectoryAsync(string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = stack.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        try { total += new FileInfo(file).Length; }
                        catch { /* skip inaccessible file */ }
                    }

                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        try
                        {
                            var attributes = File.GetAttributes(child);
                            if ((attributes & FileAttributes.ReparsePoint) == 0)
                                stack.Push(child);
                        }
                        catch { /* skip inaccessible directory */ }
                    }
                }
                catch { /* skip inaccessible directory */ }
            }

            return total;
        }, cancellationToken);

    public void ExitSearch()
    {
        CancelSearch();
        IsSearchActive = false;
        OnPropertyChanged(nameof(CanBatchRename));
        IsSearching = false;
        SearchStatus = string.Empty;
        FilterText = string.Empty;
        UpdateTitle();
        _searchResults.Clear();
        VisibleItems = [];
        ApplySortAndFilter();
        OnPropertyChanged(nameof(ItemCountText));
    }

    public async Task StartSearchAsync(string? query = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => StartSearchAsync(query)).ConfigureAwait(true);
            return;
        }

        query ??= FilterText;
        if (string.IsNullOrWhiteSpace(query))
        {
            ExitSearch();
            return;
        }

        if (IsTrash)
        {
            FilterText = query.Trim();
            return;
        }

        CancelSearch();
        _searchCts = new CancellationTokenSource();
        var cancellationToken = _searchCts.Token;

        IsSearchActive = true;
        OnPropertyChanged(nameof(CanBatchRename));
        IsSearching = true;
        HasError = false;
        Title = L10n.T("搜索结果");
        _searchResults.Clear();
        VisibleItems = _searchResults;
        SearchStatus = L10n.T("正在搜索…");
        OnPropertyChanged(nameof(ItemCountText));

        var options = new SearchOptions(query.Trim(), ShowHidden, _services.Settings.Current.MaxSearchResults,
            _services.Settings.Current.SearchDepth);
        var rootPath = SearchWholeDisk ? FileSystem.RootPath : CurrentPath;
        var count = 0;

        try
        {
            await foreach (var entry in _services.Search.SearchAsync(FileSystem, rootPath, options, null, cancellationToken)
                               .ConfigureAwait(true))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var parent = FileSystem.GetParent(entry.Path);
                _searchResults.Add(new FileItemViewModel(entry, FileSystem.GetDisplayPath(parent)));
                count++;
                if (count % 20 == 0)
                    SearchStatus = L10n.Format("正在搜索… 已找到 {0} 个", count);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                SortSearchResults();
                SearchStatus = L10n.Format("找到 {0} 个结果", count);
                RememberSearch(query.Trim());
                await ApplySearchGitStatusAsync(_searchResults.ToList(), cancellationToken).ConfigureAwait(true);
                if (!cancellationToken.IsCancellationRequested)
                    SearchStatus = L10n.Format("找到 {0} 个结果", count);
            }
        }
        catch (OperationCanceledException)
        {
            SearchStatus = L10n.Format("已停止 · {0} 个结果", count);
        }
        catch (Exception ex)
        {
            SearchStatus = L10n.T("搜索失败");
            Toast(L10n.T("搜索失败"), ex.Message, true);
        }
        finally
        {
            IsSearching = false;
            IsEmpty = !HasError && _searchResults.Count == 0;
            EmptyHint = L10n.T("没有搜索结果");
            OnPropertyChanged(nameof(ItemCountText));
        }
    }

    private void SortSearchResults()
    {
        var comparer = new FileEntryComparer(SortColumn, SortDescending, false);
        var sorted = _searchResults.OrderBy(i => i.Entry, comparer).ToList();
        _searchResults.Clear();
        foreach (var item in sorted)
            _searchResults.Add(item);
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }

    private void ApplySortAndFilter()
    {
        IEnumerable<FileItemViewModel> items = _entries;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var query = FilterText.Trim();
            items = items.Where(i => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        var comparer = new FileEntryComparer(SortColumn, SortDescending, FoldersFirst);
        var list = items.ToList();
        list.Sort((a, b) => comparer.Compare(a.Entry, b.Entry));
        VisibleItems = list;
        OnPropertyChanged(nameof(ItemCountText));
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        IsEmpty = !IsLoading && !HasError && VisibleItems.Count == 0;
        EmptyHint = IsSearchActive
            ? L10n.T("没有搜索结果")
            : HasFilter ? L10n.T("没有匹配的项目") : L10n.T("此文件夹为空");
    }

    private void UpdateBreadcrumbs()
    {
        if (IsTrash)
        {
            Breadcrumbs = [new BreadcrumbItem(L10n.T("回收站"), "/", true)];
            return;
        }

        var items = new List<BreadcrumbItem>();
        var root = FileSystem.RootPath;
        var normalized = FileSystem.NormalizePath(CurrentPath);

        if (FileSystem.IsLocal)
        {
            var pathRoot = Path.GetPathRoot(normalized) ?? "/";
            items.Add(new BreadcrumbItem(string.IsNullOrEmpty(pathRoot) || pathRoot == "/" ? L10n.T("根目录") : pathRoot, pathRoot, false));
            var relative = normalized[pathRoot.Length..].Trim('/', '\\');
            var accumulated = pathRoot;
            foreach (var segment in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
            {
                accumulated = Path.Combine(accumulated, segment);
                items.Add(new BreadcrumbItem(segment, accumulated, false));
            }
        }
        else
        {
            items.Add(new BreadcrumbItem(FileSystem.DisplayName, root, false));
            var relative = PosixPath.RelativeTo(normalized, root);
            var accumulated = root;
            foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                accumulated = PosixPath.Combine(accumulated, segment);
                items.Add(new BreadcrumbItem(segment, accumulated, false));
            }
        }

        if (items.Count > 0)
            items[^1] = items[^1] with { IsLast = true };
        Breadcrumbs = items;
    }

    private void UpdateTitle()
    {
        if (IsTrash)
        {
            Title = L10n.T("回收站");
            return;
        }

        if (IsSearchActive)
        {
            Title = L10n.T("搜索结果");
            return;
        }

        if (FileSystem.IsRoot(CurrentPath))
        {
            Title = FileSystem.IsLocal
                ? (CurrentPath == "/" ? L10n.T("根目录") : KnownFolders.DisplayNameForPath(CurrentPath))
                : FileSystem.DisplayName;
            return;
        }

        Title = FileSystem.IsLocal
            ? KnownFolders.DisplayNameForPath(CurrentPath)
            : FileSystem.GetName(CurrentPath);
    }

    private void UpdateLocationText()
    {
        if (IsEditingLocation)
            return;
        LocationText = BuildEditablePath();
    }

    private string BuildEditablePath()
    {
        if (IsTrash)
            return L10n.T("回收站");
        if (FileSystem.IsLocal)
            return CurrentPath;
        var display = FileSystem.GetDisplayPath(CurrentPath);
        return display.Contains("://", StringComparison.Ordinal) ? display : $"{FileSystem.Scheme}://{display}";
    }

    private async Task UpdateFreeSpaceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var space = await FileSystem.GetSpaceAsync(CurrentPath, cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
                return;
            if (space.IsKnown)
            {
                FreeSpaceText = L10n.Format("{0} 可用", Humanize.Size(space.Free!.Value));
                FreeSpaceFraction = space.UsedFraction ?? 0;
            }
            else
            {
                FreeSpaceText = string.Empty;
                FreeSpaceFraction = 0;
            }
        }
        catch
        {
            FreeSpaceText = string.Empty;
        }
    }

    private void BumpRecent()
    {
        if (IsTrash || HasError)
            return;
        try
        {
            _services.Recent.Add(FileSystem, CurrentPath, Title);
        }
        catch { /* ignore */ }
    }

    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanCreateFolder));
        OnPropertyChanged(nameof(CanCreateFile));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanBatchRename));
        OnPropertyChanged(nameof(CanCompress));
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(CanChecksum));
        OnPropertyChanged(nameof(CanOpenArchive));
        OnPropertyChanged(nameof(CanShowGitStatus));
        OnPropertyChanged(nameof(CanOpenWith));
        OnPropertyChanged(nameof(CanOpenInNewWindow));
        OnPropertyChanged(nameof(CanOpenTerminal));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CanEmptyTrash));
        OnPropertyChanged(nameof(CanSelectAll));
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(DisplayPath));
        OnPropertyChanged(nameof(IsTrash));
        OnPropertyChanged(nameof(IsRemote));
            OnPropertyChanged(nameof(IsArchive));
        OnPropertyChanged(nameof(FsBadgeText));
    }

    private async Task RestoreAndOpenAsync(FileItemViewModel item)
    {
        try
        {
            var destination = await _services.Trash.RestoreAsync(item.Path).ConfigureAwait(true);
            Toast(L10n.T("已恢复"), destination, false);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Toast(L10n.T("恢复失败"), ex.Message, true);
        }
    }

    private async Task OpenRemoteFileAsync(FileItemViewModel item)
    {
        if (IsOpeningRemote)
            return;

        IsOpeningRemote = true;
        try
        {
            var safeName = string.Join("_", item.Name.Split(Path.GetInvalidFileNameChars()));
            var directory = Path.Combine(AppPaths.CacheDirectory, "open", FileSystem.Id);
            Directory.CreateDirectory(directory);
            var localPath = Path.Combine(directory, safeName);

            Toast(L10n.T("正在下载"), item.Name, false);
            await using (var input = await FileSystem.OpenReadAsync(item.Path).ConfigureAwait(true))
            await using (var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output).ConfigureAwait(true);
            }

            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(localPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* best effort */ }
            }

            if (!_services.OpenWith.TryOpenDefault(localPath))
                await _services.Shell.OpenAsync(localPath).ConfigureAwait(true);
            Toast(L10n.T("已下载并打开"), localPath, false);
        }
        catch (Exception ex)
        {
            Toast(L10n.T("无法打开远程文件"), ex.Message, true);
        }
        finally
        {
            IsOpeningRemote = false;
        }
    }

    private void StartThumbnailLoading()
    {
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = null;

        if (!IsGrid || !_services.Settings.Current.ShowThumbnails || !FileSystem.IsLocal || _entries.Count > 700)
            return;

        _thumbCts = new CancellationTokenSource();
        _ = LoadThumbnailsAsync(_thumbCts.Token);
    }

    private async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        foreach (var item in _entries)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (item.IsDirectory || item.Thumbnail is not null)
                continue;
            if (!MimeTypes.IsImage(item.Extension))
                continue;

            var bitmap = await _services.Thumbnails.GetAsync(FileSystem, item.Entry, 72, cancellationToken)
                .ConfigureAwait(true);
            if (bitmap is not null && !cancellationToken.IsCancellationRequested)
                item.Thumbnail = bitmap;
        }
    }

    private void StartWatcher()
    {
        StopWatcher();
        if (_disposed || !FileSystem.IsLocal || IsTrash || !Directory.Exists(CurrentPath))
            return;

        try
        {
            var watcher = new FileSystemWatcher(CurrentPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                               NotifyFilters.Size | NotifyFilters.Attributes,
                EnableRaisingEvents = true,
            };
            watcher.Created += (_, _) => ScheduleWatchRefresh();
            watcher.Changed += (_, _) => ScheduleWatchRefresh();
            watcher.Deleted += (_, _) => ScheduleWatchRefresh();
            watcher.Renamed += (_, _) => ScheduleWatchRefresh();
            watcher.Error += (_, _) => ScheduleWatchRefresh();
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            Log.Warn($"无法监听目录变化: {CurrentPath}", ex);
        }
    }

    private void StopWatcher()
    {
        _watchDebounce?.Cancel();
        _watchDebounce?.Dispose();
        _watchDebounce = null;

        if (_watcher is null)
            return;

        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch { /* ignore */ }
        _watcher = null;
    }

    private void ScheduleWatchRefresh()
    {
        if (_disposed)
            return;

        _watchDebounce?.Cancel();
        _watchDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _watchDebounce = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(320, token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && !IsLoading && !IsSearchActive)
                        _ = RefreshAsync();
                });
            }
            catch (OperationCanceledException) { /* superseded */ }
            catch (ObjectDisposedException) { /* superseded */ }
        }, token);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSettingsChanged(sender, e));
            return;
        }

        var settings = _services.Settings.Current;
        if (settings.ShowHidden != _lastShowHidden)
        {
            _lastShowHidden = settings.ShowHidden;
            ShowHidden = settings.ShowHidden;
            _ = LoadAsync().ConfigureAwait(true);
        }

        RefreshColumnLayout();

        var gitEnabledChanged = settings.EnableGitIntegration != _lastGitEnabled;
        var gitDisplayChanged = settings.ShowGitFileBadges != _lastGitBadges ||
                                settings.ShowGitCleanCheck != _lastGitCleanCheck ||
                                settings.ShowGitRepositoryBadges != _lastGitRepositoryBadges ||
                                settings.ShowGitSubmoduleBadges != _lastGitSubmoduleBadges ||
                                settings.DimGitIgnored != _lastGitDimIgnored;
        var gitStatusBarChanged = settings.ShowGitStatusBar != _lastGitStatusBar;
        _lastGitEnabled = settings.EnableGitIntegration;
        _lastGitBadges = settings.ShowGitFileBadges;
        _lastGitCleanCheck = settings.ShowGitCleanCheck;
        _lastGitRepositoryBadges = settings.ShowGitRepositoryBadges;
        _lastGitSubmoduleBadges = settings.ShowGitSubmoduleBadges;
        _lastGitDimIgnored = settings.DimGitIgnored;

        if (gitStatusBarChanged)
        {
            _lastGitStatusBar = settings.ShowGitStatusBar;
            OnPropertyChanged(nameof(ShowGitStatusBar));
        }

        if (gitEnabledChanged)
        {
            OnPropertyChanged(nameof(CanShowGitStatus));
            OnPropertyChanged(nameof(ShowGitStatusBar));
            if (settings.EnableGitIntegration)
                _ = LoadAsync().ConfigureAwait(true);
            else
                ApplyGitStatus(null);
        }
        else if (gitDisplayChanged)
        {
            ApplyGitStatus(_gitStatus);
        }
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanPaste));
    }

    private string? ValidateNewName(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return L10n.T("名称不能为空");
        if (value is "." or "..")
            return L10n.T("名称无效");
        if (value.IndexOfAny(['/', '\\', '\0']) >= 0)
            return L10n.T("名称中不能包含路径分隔符");
        if (value.Length > 255)
            return L10n.T("名称过长");
        return null;
    }

    private string? ValidateName(string value, string current)
    {
        if (string.Equals(value, current, StringComparison.Ordinal))
            return null;
        return ValidateNewName(value);
    }

    private bool IsNameFree(string name) => !IsNameTaken(name);

    private bool IsNameTaken(string name)
    {
        var path = Path.Combine(CurrentPath, name);
        return File.Exists(path) || Directory.Exists(path);
    }

    private string BuildDeleteMessage(IReadOnlyList<OperationSource> sources)
    {
        var names = string.Join(L10n.T("、"), sources.Take(3).Select(s => s.Name));
        if (sources.Count > 3)
            names += L10n.Format(" 等 {0} 个项目", sources.Count);
        return L10n.Format("“{0}”将被永久删除，此操作无法撤销。", names);
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => L10n.T("没有权限访问此位置"),
        DirectoryNotFoundException => L10n.T("文件夹不存在或已被移动"),
        FileNotFoundException => L10n.T("文件不存在"),
        _ => ex.Message,
    };

    private void Toast(string title, string message, bool error)
        => NotificationRequested?.Invoke(title, message, error);

    public void Notify(string title, string message, bool error = false) => Toast(title, message, error);

    public async Task EnqueueDropTransferAsync(IReadOnlyList<OperationSource> sources, string destinationPath,
        bool move)
    {
        if (sources.Count == 0 || IsTrash)
            return;
        var preview = await PreviewTransferAsync(sources, FileSystem, destinationPath, move).ConfigureAwait(true);
        if (preview == ConflictChoice.Cancel)
            return;
        _services.Operations.Copy(sources, FileSystem, destinationPath, move, preview);
        OperationStarted?.Invoke(this);
        Toast(move ? L10n.T("正在移动") : L10n.T("正在复制"), L10n.Format("{0} 个项目", sources.Count), false);
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
                // A failed probe is handled by the normal conflict resolver.
            }
        }

        if (conflicts.Count == 0)
            return null;

        var choice = await _services.Dialogs.ShowTransferPreviewAsync(new TransferPreviewRequest(
            move, sources.Count, destinationFileSystem.GetDisplayPath(destinationDirectory),
            conflicts.Count, conflicts.Take(12).ToList(), scanned)).ConfigureAwait(true);
        return choice ?? ConflictChoice.Cancel;
    }

    /// <summary>Drop onto a Trash pane: move local sources to the XDG trash.</summary>
    public void EnqueueTrashDrop(IReadOnlyList<OperationSource> sources)
    {
        if (sources.Count == 0 || !_services.Trash.IsSupported)
            return;
        if (sources.Any(s => !s.FileSystem.IsLocal || s.FileSystem is TrashFileSystem))
            return;

        _services.Operations.Delete(sources, permanent: false);
        OperationStarted?.Invoke(this);
        Toast(L10n.T("正在移到回收站"), L10n.Format("{0} 个项目", sources.Count), false);
    }

    /// <summary>Shift+drop onto a Trash pane: delete the sources permanently.</summary>
    public async Task EnqueuePermanentDropAsync(IReadOnlyList<OperationSource> sources)
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
        OperationStarted?.Invoke(this);
        Toast(L10n.T("正在删除"), L10n.Format("{0} 个项目", sources.Count), false);
    }

    public void Activate()
    {
        if (!IsActive)
            IsActive = true;
    }

    public void Deactivate() => IsActive = false;

    public void SetDropTarget(bool value, bool move = false, string? destination = null,
        bool trash = false, bool permanent = false)
    {
        var hint = !value
            ? string.Empty
            : permanent
                ? L10n.T("松开以永久删除")
                : trash ? L10n.T("松开以移到回收站")
                : move ? L10n.T("松开以移动到") : L10n.T("松开以复制到");
        var destinationText = value ? destination ?? string.Empty : string.Empty;
        var showsModifiers = value && !trash && !permanent;

        if (IsDropTarget == value && DropHint == hint &&
            DropDestination == destinationText && DropShowsModifiers == showsModifiers)
            return;

        IsDropTarget = value;
        DropHint = hint;
        DropDestination = destinationText;
        DropShowsModifiers = showsModifiers;
    }

    public IReadOnlyList<FileItemViewModel> SelectedItems => _selected;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _gitCts?.Cancel();
        _gitCts?.Dispose();
        _gitCts = null;
        CancelSearch();
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        StopWatcher();
        _services.Settings.Changed -= OnSettingsChanged;
        _services.Clipboard.Changed -= OnClipboardChanged;
    }

    private sealed record HistoryEntry(IFileSystem FileSystem, string Path);
}
