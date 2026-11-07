namespace Ordoa.Core.Models;

public enum FileColor
{
    None,
    Red,
    Orange,
    Yellow,
    Green,
    Blue,
    Purple,
    Gray,
}

/// <summary>Sidecar metadata for a single file or folder.</summary>
public sealed class FileTagInfo
{
    public FileColor Color { get; set; }
    public List<string> Tags { get; set; } = [];

    public bool IsEmpty => Color == FileColor.None && Tags.Count == 0;

    public FileTagInfo Clone() => new()
    {
        Color = Color,
        Tags = [.. Tags],
    };
}
