using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Ordoa.Core.Models;

namespace Ordoa.App.Services;

public enum FileVisualKind
{
    Folder,
    Application,
    File,
    Image,
    Audio,
    Video,
    Archive,
    Code,
    Text,
    Pdf,
    Document,
    Sheet,
    Slide,
}

public readonly record struct FileVisual(Geometry Geometry, IBrush Brush);

public static class FileIconCatalog
{
    private static readonly Dictionary<FileVisualKind, FileVisual> Cache = new();

    private static readonly Dictionary<FileVisualKind, (string Data, string Color)> Definitions = new()
    {
        [FileVisualKind.Folder] = ("M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z", "#E0A63C"),
        [FileVisualKind.Application] = ("M16,20H20V16H16M16,14H20V10H16M10,8H14V4H10M16,8H20V4H16M10,14H14V10H10M4,8H8V4H4M4,14H8V10H4M4,20H8V16H4M10,20H14V16H10V20Z", "#4D8FD6"),
        [FileVisualKind.File] = ("M13,9V3.5L18.5,9M6,2C4.89,2 4,2.89 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2H6Z", "#87918B"),
        [FileVisualKind.Image] = ("M8.5,13.5L11,16.5L14.5,12L19,18H5M21,19V5C21,3.89 20.1,3 19,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19Z", "#2FAE9B"),
        [FileVisualKind.Audio] = ("M12,3V13.55C11.41,13.21 10.73,13 10,13A3,3 0 0,0 7,16A3,3 0 0,0 10,19A3,3 0 0,0 13,16V7H17V3H12Z", "#D969A7"),
        [FileVisualKind.Video] = ("M17,10.5V7A1,1 0 0,0 16,6H4A1,1 0 0,0 3,7V17A1,1 0 0,0 4,18H16A1,1 0 0,0 17,17V13.5L21,17.5V6.5L17,10.5Z", "#5E8BDE"),
        [FileVisualKind.Archive] = ("M20,6H12L10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6M14,17H12V15H10V13H12V11H10V9H12V11H14V13H12V15H14V17Z", "#A17BE0"),
        [FileVisualKind.Code] = ("M8,3A2,2 0 0,0 6,5V9A2,2 0 0,1 4,11A2,2 0 0,1 6,13V17A2,2 0 0,0 8,19H10V17H8V13A2,2 0 0,0 6,11A2,2 0 0,0 8,9V5H10V3H8M16,3A2,2 0 0,1 18,5V9A2,2 0 0,0 20,11A2,2 0 0,0 18,13V17A2,2 0 0,1 16,19H14V17H16V13A2,2 0 0,1 18,11A2,2 0 0,1 16,9V5H14V3H16Z", "#4FAF6E"),
        [FileVisualKind.Text] = ("M14,17H7V15H14M17,13H7V11H17M17,9H7V7H17M19,3H5C3.89,3 3,3.89 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5C21,3.89 20.1,3 19,3Z", "#7D8794"),
        [FileVisualKind.Pdf] = ("M19,3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5C21,3.9 20.1,3 19,3M9.5,15H8V9H11A1.5,1.5 0 0,1 12.5,10.5V12A1.5,1.5 0 0,1 11,13.5H9.5V15M9.5,10V12H11V10H9.5M17.5,15H16V13.5H14.5V15H13V9H16.5A1.5,1.5 0 0,1 18,10.5V13.5A1.5,1.5 0 0,1 16.5,15H17.5M16,10H14.5V12H16V10Z", "#E05D5D"),
        [FileVisualKind.Document] = ("M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20M8,12H16V14H8V12M8,16H16V18H8V16Z", "#4E84D8"),
        [FileVisualKind.Sheet] = ("M19,3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5C21,3.9 20.1,3 19,3M19,19H5V8H19V19M5,6V5H19V6H5M7,10H10V12H7V10M12,10H17V12H12V10M7,14H10V16H7V14M12,14H17V16H12V14Z", "#4B9C6B"),
        [FileVisualKind.Slide] = ("M21,16V4C21,2.89 20.1,2 19,2H5A2,2 0 0,0 3,4V16A2,2 0 0,0 5,18H10.5V20H8V22H16V20H13.5V18H19A2,2 0 0,0 21,16M19,16H5V4H19V16Z", "#DD7A45"),
    };

    public static FileVisual For(FileEntry entry, bool isApplication = false)
    {
        var kind = isApplication ? FileVisualKind.Application : Classify(entry);
        if (Cache.TryGetValue(kind, out var visual))
            return visual;

        var (data, color) = Definitions[kind];
        visual = new FileVisual(Geometry.Parse(data), new SolidColorBrush(Color.Parse(color)));
        Cache[kind] = visual;
        return visual;
    }

    public static FileVisual ForKind(FileVisualKind kind)
    {
        if (Cache.TryGetValue(kind, out var visual))
            return visual;
        var (data, color) = Definitions[kind];
        visual = new FileVisual(Geometry.Parse(data), new SolidColorBrush(Color.Parse(color)));
        Cache[kind] = visual;
        return visual;
    }

    public static FileVisualKind Classify(FileEntry entry)
    {
        if (entry.IsDirectory)
            return FileVisualKind.Folder;

        var extension = entry.Extension;
        if (MimeTypes.IsImage(extension))
            return FileVisualKind.Image;
        if (MimeTypes.IsArchive(extension))
            return FileVisualKind.Archive;

        return extension switch
        {
            "mp3" or "flac" or "ogg" or "oga" or "opus" or "wav" or "m4a" or "aac" or "wma" or "mid" or "midi" => FileVisualKind.Audio,
            "mp4" or "mkv" or "webm" or "avi" or "mov" or "wmv" or "flv" or "m4v" or "mpg" or "mpeg" => FileVisualKind.Video,
            "pdf" => FileVisualKind.Pdf,
            "doc" or "docx" or "odt" or "rtf" or "epub" => FileVisualKind.Document,
            "xls" or "xlsx" or "ods" or "csv" or "tsv" => FileVisualKind.Sheet,
            "ppt" or "pptx" or "odp" => FileVisualKind.Slide,
            "txt" or "md" or "log" or "ini" or "conf" or "cfg" or "json" or "yaml" or "yml" or "toml" or "xml" => FileVisualKind.Text,
            "js" or "mjs" or "ts" or "tsx" or "jsx" or "py" or "rb" or "go" or "rs" or "java" or "kt" or "c" or "h" or "cc" or "cpp" or "hpp" or "cs" or "fs" or "vb" or "php" or "sh" or "bash" or "zsh" or "fish" or "lua" or "swift" or "sql" or "html" or "htm" or "css" or "scss" => FileVisualKind.Code,
            _ => FileVisualKind.File,
        };
    }

    public static Geometry? Resource(string key)
    {
        if (Application.Current?.TryFindResource(key, out var value) == true && value is Geometry geometry)
            return geometry;
        return null;
    }
}
