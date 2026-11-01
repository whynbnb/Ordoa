namespace Ordoa.Core.Models;

[Flags]
public enum FileSystemCapabilities
{
    None = 0,
    List = 1 << 0,
    Read = 1 << 1,
    Write = 1 << 2,
    CreateDirectory = 1 << 3,
    Delete = 1 << 4,
    Rename = 1 << 5,
    Move = 1 << 6,
    Copy = 1 << 7,
    Symlinks = 1 << 8,
    UnixPermissions = 1 << 9,
    Trash = 1 << 10,
    SpaceInfo = 1 << 11,
    ServerSideCopy = 1 << 12,
    SetModifiedTime = 1 << 13,
}

public static class FileSystemCapabilitiesExtensions
{
    public static bool Has(this FileSystemCapabilities value, FileSystemCapabilities flag)
        => (value & flag) == flag;
}

public readonly record struct SpaceInfo(long? Free, long? Total)
{
    public static SpaceInfo Unknown => new(null, null);
    public bool IsKnown => Free.HasValue && Total.HasValue;
    public double? UsedFraction => IsKnown && Total > 0 ? 1d - (double)Free!.Value / Total!.Value : null;
}
