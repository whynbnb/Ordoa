using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.App.Services;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

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

    public string SizeText => Entry.IsDirectory ? "—" : Humanize.Size(Entry.Size);
    public string ModifiedText => Humanize.Date(Entry.Modified);
    public string TypeText => Entry.TypeDescription;
    public string GridSubtitle => Entry.IsDirectory ? "文件夹" : Humanize.Size(Entry.Size);
    public string? OriginalPath => Entry.OriginalPath;
    public string? LinkTarget => Entry.LinkTarget;

    public string TooltipText
    {
        get
        {
            var text = $"{Name}\n{DisplayLocation}";
            if (!string.IsNullOrEmpty(LinkTarget))
                text += $"\n→ {LinkTarget}";
            if (Entry.IsDirectory && Entry.OriginalPath is not null)
                text += $"\n原位置: {Entry.OriginalPath}";
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
