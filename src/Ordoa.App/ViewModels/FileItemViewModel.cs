using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.App.Services;
using Ordoa.Core.Models;
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

    public bool HasThumbnail => Thumbnail is not null;
    public bool IsTrashItem => Entry.OriginalPath is not null;
    public string? DeletedText => Entry.DeletedAt is null ? null : Humanize.Date(Entry.DeletedAt);
}
