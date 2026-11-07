using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.App.Services;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;
using Ordoa.Core.Localization;

namespace Ordoa.App.ViewModels;

public sealed partial class FileItemViewModel : ObservableObject
{
    private readonly FileVisual _visual;

    public FileItemViewModel(FileEntry entry, string? displayLocation = null)
    {
        Entry = entry;
        _visual = FileIconCatalog.For(entry);
        DisplayLocation = displayLocation ?? entry.Path;
    }

    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public bool IsDirectory => Entry.IsDirectory;
    public bool IsSymlink => Entry.IsSymlink;
    public bool IsHidden => Entry.IsHidden;
    public string Extension => Entry.Extension;
    public string DisplayLocation { get; }
    public Geometry Icon => _visual.Geometry;
    public IBrush IconBrush => _visual.Brush;

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(GridSubtitle));
        OnPropertyChanged(nameof(ModifiedText));
        OnPropertyChanged(nameof(GitTooltip));
        OnPropertyChanged(nameof(TooltipText));
    }

    public string SizeText => Entry.IsDirectory ? "—" : Humanize.Size(Entry.Size);
    public string ModifiedText => Humanize.Date(Entry.Modified);
    public string TypeText => Entry.TypeDescription;
    public string PermissionText => FilePermission.Format(Entry.UnixMode);
    public string OwnerText => Entry.Owner ?? "—";
    public string OwnershipText => Entry.Owner is null && Entry.Group is null
        ? L10n.T("未知所有者")
        : $"{Entry.Owner ?? "—"}:{Entry.Group ?? "—"}";
    public string GridSubtitle => Entry.IsDirectory ? L10n.T("文件夹") : Humanize.Size(Entry.Size);
    public string? OriginalPath => Entry.OriginalPath;
    public string? LinkTarget => Entry.LinkTarget;

    public string TooltipText
    {
        get
        {
            var text = $"{Name}\n{DisplayLocation}";
            if (!string.IsNullOrEmpty(LinkTarget))
                text += $"\n→ {LinkTarget}";
            if (Entry.UnixMode is not null)
                text += $"\n{L10n.T("权限")}: {PermissionText}";
            if (Entry.Owner is not null || Entry.Group is not null)
                text += $"\n{L10n.T("所有者")}: {OwnershipText}";
            if (GitTooltip is not null)
                text += $"\n{GitTooltip}";
            if (Entry.IsDirectory && Entry.OriginalPath is not null)
                text += "\n" + L10n.Format("原位置: {0}", Entry.OriginalPath);
            return text;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _thumbnailRequested;

    private GitFileChange? _gitChange;
    private bool _isGitDirectoryDirty;
    private bool _isGitTrackedClean;
    private bool _isGitIgnored;
    private bool _isGitRepository;
    private bool _isGitSubmodule;
    private bool _gitBadgesVisible = true;
    private bool _showGitClean = true;
    private bool _showGitRepository = true;
    private bool _showGitSubmodule = true;
    private bool _dimGitIgnored = true;

    public bool IsGitDirectoryDirty => _isGitDirectoryDirty;
    public bool IsGitIgnored => _isGitIgnored;
    public bool IsGitRepository => _isGitRepository;
    public bool IsGitSubmodule => _isGitSubmodule;
    public bool IsDimmed => IsHidden || _dimGitIgnored && _isGitIgnored;

    public bool ShowGitBadge => _gitBadgesVisible &&
        (_gitChange is not null || _isGitDirectoryDirty ||
         _showGitClean && _isGitTrackedClean ||
         _showGitRepository && _isGitRepository ||
         _showGitSubmodule && _isGitSubmodule);

    public string GitBadgeText
    {
        get
        {
            if (_showGitSubmodule && _isGitSubmodule)
                return "S";
            if (_showGitRepository && _isGitRepository)
                return "GIT";
            if (_gitChange is not null)
                return _gitChange.Code;
            if (_showGitClean && _isGitTrackedClean)
                return "✓";
            return _isGitDirectoryDirty ? "●" : string.Empty;
        }
    }

    public IBrush? GitBadgeBrush
    {
        get
        {
            if (_isGitSubmodule)
                return _gitChange is not null ? BrushFor(_gitChange) : GitBrushes.Submodule;
            if (_isGitRepository)
                return _gitChange is not null ? BrushFor(_gitChange) : GitBrushes.Repository;
            if (_gitChange is not null)
                return BrushFor(_gitChange);
            if (_isGitTrackedClean)
                return GitBrushes.Added;
            return _isGitDirectoryDirty ? GitBrushes.Modified : null;
        }
    }

    public string? GitTooltip
    {
        get
        {
            var lines = new List<string>();
            if (_isGitRepository)
                lines.Add(L10n.T("Git 仓库"));
            if (_isGitSubmodule)
                lines.Add(L10n.T("Git 子模块"));
            if (_gitChange is not null)
                lines.Add($"{_gitChange.Description}\n{_gitChange.RelativePath}");
            else if (_isGitTrackedClean)
                lines.Add(L10n.T("已提交，工作区干净"));
            if (_isGitDirectoryDirty)
                lines.Add(L10n.T("此文件夹包含未提交的变更"));
            if (_isGitIgnored)
                lines.Add(L10n.T("已被 Git 忽略"));
            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }
    }

    public void SetGitState(GitFileChange? change, bool directoryDirty = false,
        bool trackedClean = false, bool ignored = false, bool repository = false,
        bool submodule = false)
    {
        if (ReferenceEquals(_gitChange, change) &&
            _isGitDirectoryDirty == directoryDirty &&
            _isGitTrackedClean == trackedClean &&
            _isGitIgnored == ignored &&
            _isGitRepository == repository &&
            _isGitSubmodule == submodule)
            return;

        _gitChange = change;
        _isGitDirectoryDirty = directoryDirty;
        _isGitTrackedClean = trackedClean;
        _isGitIgnored = ignored;
        _isGitRepository = repository;
        _isGitSubmodule = submodule;
        NotifyGitChanged();
    }

    public void SetGitDisplay(bool badges, bool cleanCheck, bool repositoryBadges,
        bool submoduleBadges, bool dimIgnored)
    {
        if (_gitBadgesVisible == badges && _showGitClean == cleanCheck &&
            _showGitRepository == repositoryBadges && _showGitSubmodule == submoduleBadges &&
            _dimGitIgnored == dimIgnored)
            return;

        _gitBadgesVisible = badges;
        _showGitClean = cleanCheck;
        _showGitRepository = repositoryBadges;
        _showGitSubmodule = submoduleBadges;
        _dimGitIgnored = dimIgnored;
        NotifyGitChanged();
        OnPropertyChanged(nameof(IsDimmed));
    }

    private void NotifyGitChanged()
    {
        OnPropertyChanged(nameof(ShowGitBadge));
        OnPropertyChanged(nameof(IsGitDirectoryDirty));
        OnPropertyChanged(nameof(IsGitIgnored));
        OnPropertyChanged(nameof(IsGitRepository));
        OnPropertyChanged(nameof(IsGitSubmodule));
        OnPropertyChanged(nameof(GitBadgeText));
        OnPropertyChanged(nameof(GitBadgeBrush));
        OnPropertyChanged(nameof(GitTooltip));
        OnPropertyChanged(nameof(TooltipText));
    }

    private static IBrush BrushFor(GitFileChange change) => change.Kind switch
    {
        GitChangeKind.Untracked or GitChangeKind.Added => GitBrushes.Added,
        GitChangeKind.Deleted or GitChangeKind.Conflicted => GitBrushes.Deleted,
        GitChangeKind.Renamed or GitChangeKind.Copied => GitBrushes.Renamed,
        GitChangeKind.TypeChanged => GitBrushes.Renamed,
        _ => GitBrushes.Modified,
    };

    public bool HasThumbnail => Thumbnail is not null;
    public bool IsTrashItem => Entry.OriginalPath is not null;
    public string? DeletedText => Entry.DeletedAt is null ? null : Humanize.Date(Entry.DeletedAt);
}

internal static class GitBrushes
{
    public static readonly IBrush Modified = new SolidColorBrush(Color.Parse("#C08A3E"));
    public static readonly IBrush Added = new SolidColorBrush(Color.Parse("#3E9B72"));
    public static readonly IBrush Deleted = new SolidColorBrush(Color.Parse("#C26A6A"));
    public static readonly IBrush Renamed = new SolidColorBrush(Color.Parse("#5A9BD5"));
    public static readonly IBrush Submodule = new SolidColorBrush(Color.Parse("#8E6AD6"));
    public static readonly IBrush Repository = new SolidColorBrush(Color.Parse("#6A5ACD"));
}
