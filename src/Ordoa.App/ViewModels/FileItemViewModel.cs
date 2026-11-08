using Avalonia;
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

    public FileItemViewModel(FileEntry entry, string? displayLocation = null, bool isLocal = true)
    {
        Entry = entry;
        IsApplicationBundle = MacAppBundles.IsApplication(entry, isLocal);
        _visual = FileIconCatalog.For(entry, IsApplicationBundle);
        DisplayLocation = displayLocation ?? entry.Path;
    }

    /// <summary>Creates a non-selectable heading row for grouped list views.</summary>
    public static FileItemViewModel CreateGroupHeader(string text, int count)
    {
        var label = count > 0
            ? $"{text} · {L10n.Format("{0} 项", count)}"
            : text;
        return new FileItemViewModel(new FileEntry { Name = text, Path = string.Empty })
        {
            IsGroupHeader = true,
            GroupHeaderText = label,
        };
    }

    public bool IsGroupHeader { get; private init; }
    public string GroupHeaderText { get; private init; } = string.Empty;

    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public bool IsDirectory => Entry.IsDirectory;
    public bool IsApplicationBundle { get; }
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
        OnPropertyChanged(nameof(TagsColumnText));
        OnPropertyChanged(nameof(ModifiedText));
        OnPropertyChanged(nameof(GitTooltip));
        OnPropertyChanged(nameof(TooltipText));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private string? _folderSizeText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private bool _isCalculatingFolderSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Indent))]
    private int _depth;

    [ObservableProperty]
    private bool _isTreeExpanded;

    [ObservableProperty]
    private bool _showTreeExpander;

    public Thickness Indent => new(Depth * 16 + 2, 0, 0, 0);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTagColor))]
    [NotifyPropertyChangedFor(nameof(TagTooltip))]
    private IBrush? _tagBrush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    [NotifyPropertyChangedFor(nameof(TagTooltip))]
    private string _tagsText = string.Empty;

    public bool HasTagColor => TagBrush is not null;
    public bool HasTags => TagsText.Length > 0;

    /// <summary>Compact text for the optional tags column.</summary>
    public string TagsColumnText => TagsText.Length > 0 ? TagsText : TagText;

    public string? TagTooltip
    {
        get
        {
            var parts = new List<string>();
            if (HasTagColor)
            {
                parts.Add(L10n.T("颜色") + ": " + TagText);
            }
            if (HasTags)
                parts.Add(L10n.T("标签") + ": " + TagsText);
            return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
        }
    }

    private string _tagText = string.Empty;

    public string TagText => _tagText;

    public void ApplyTag(FileColor color, IReadOnlyList<string> tags)
    {
        TagBrush = color switch
        {
            FileColor.Red => TagBrushes.Red,
            FileColor.Orange => TagBrushes.Orange,
            FileColor.Yellow => TagBrushes.Yellow,
            FileColor.Green => TagBrushes.Green,
            FileColor.Blue => TagBrushes.Blue,
            FileColor.Purple => TagBrushes.Purple,
            FileColor.Gray => TagBrushes.Gray,
            _ => null,
        };
        _tagText = color switch
        {
            FileColor.Red => L10n.T("红色"),
            FileColor.Orange => L10n.T("橙色"),
            FileColor.Yellow => L10n.T("黄色"),
            FileColor.Green => L10n.T("绿色"),
            FileColor.Blue => L10n.T("蓝色"),
            FileColor.Purple => L10n.T("紫色"),
            FileColor.Gray => L10n.T("灰色"),
            _ => string.Empty,
        };
        TagsText = string.Join(", ", tags);
        OnPropertyChanged(nameof(TagText));
        OnPropertyChanged(nameof(TagsColumnText));
        OnPropertyChanged(nameof(TooltipText));
    }

    public string SizeText => Entry.IsDirectory
        ? FolderSizeText ?? (IsCalculatingFolderSize ? "…" : "—")
        : Humanize.Size(Entry.Size);
    public string ModifiedText => Humanize.Date(Entry.Modified);
    public string TypeText => IsApplicationBundle ? L10n.T("应用程序") : Entry.TypeDescription;
    public string PermissionText => FilePermission.Format(Entry.UnixMode);
    public string OwnerText => Entry.Owner ?? "—";
    public string OwnershipText => Entry.Owner is null && Entry.Group is null
        ? L10n.T("未知所有者")
        : $"{Entry.Owner ?? "—"}:{Entry.Group ?? "—"}";
    public string GridSubtitle => IsApplicationBundle
        ? L10n.T("应用程序")
        : Entry.IsDirectory ? L10n.T("文件夹") : Humanize.Size(Entry.Size);
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
            if (TagTooltip is not null)
                text += $"\n{TagTooltip}";
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
    public bool IsGitIgnoredDimmed => _isGitIgnored && _dimGitIgnored;
    public bool IsGitRepository => _isGitRepository;
    public bool IsGitSubmodule => _isGitSubmodule;

    public bool ShowGitBadge => _gitBadgesVisible &&
        (_gitChange is not null || _isGitDirectoryDirty || _isGitIgnored ||
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
            if (_isGitIgnored)
                return "I";
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
            if (_isGitIgnored)
                return GitBrushes.Ignored;
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
    }

    private void NotifyGitChanged()
    {
        OnPropertyChanged(nameof(ShowGitBadge));
        OnPropertyChanged(nameof(IsGitDirectoryDirty));
        OnPropertyChanged(nameof(IsGitIgnored));
        OnPropertyChanged(nameof(IsGitIgnoredDimmed));
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
    public static readonly IBrush Ignored = new SolidColorBrush(Color.Parse("#7D8794"));
}

public static class TagBrushes
{
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#D9534F"));
    public static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#E58B2A"));
    public static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#D8B321"));
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#2E9E5B"));
    public static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#4A90D9"));
    public static readonly IBrush Purple = new SolidColorBrush(Color.Parse("#8E6AD6"));
    public static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#7D8794"));
}
