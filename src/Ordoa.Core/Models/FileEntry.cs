using Ordoa.Core.Localization;
using System.Globalization;

namespace Ordoa.Core.Models;

/// <summary>The kind of object a filesystem entry represents.</summary>
public enum EntryKind
{
    File,
    Directory,
    Device,
    Socket,
    Fifo,
    Unknown,
}

/// <summary>A single item returned by a filesystem provider.</summary>
public sealed record FileEntry
{
    public required string Name { get; init; }

    /// <summary>Provider-native path (absolute for local files, POSIX-style for remotes).</summary>
    public required string Path { get; init; }

    public EntryKind Kind { get; init; } = EntryKind.File;
    public long Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Accessed { get; init; }
    public bool IsHidden { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsSymlink { get; init; }
    public string? LinkTarget { get; init; }
    public UnixFileMode? UnixMode { get; init; }
    public string? MimeType { get; init; }
    public string? Owner { get; init; }
    public string? Group { get; init; }

    /// <summary>For items inside the trash: the path the item was deleted from.</summary>
    public string? OriginalPath { get; init; }

    /// <summary>For items inside the trash: the deletion timestamp.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public bool IsDirectory => Kind == EntryKind.Directory;

    public string Extension
    {
        get
        {
            var name = Name;
            var dot = name.LastIndexOf('.');
            return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
        }
    }

    public bool Matches(string query)
        => Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    public string TypeDescription => Kind switch
    {
        EntryKind.Directory => L10n.T("文件夹"),
        EntryKind.Device => L10n.T("设备"),
        EntryKind.Socket => L10n.T("套接字"),
        EntryKind.Fifo => L10n.T("管道"),
        EntryKind.Unknown => L10n.T("未知"),
        _ => MimeTypes.Describe(Extension),
    };
}
