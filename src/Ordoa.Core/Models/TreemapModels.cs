namespace Ordoa.Core.Models;

/// <summary>One rectangle in a treemap. Values are arbitrary units; the layout scales them to area.</summary>
public sealed record TreemapEntry(
    string Id,
    string Label,
    double Value,
    string Path,
    bool IsDirectory,
    bool IsAggregate = false);

public readonly record struct TreemapRect(double X, double Y, double Width, double Height);

public sealed record TreemapTile(TreemapEntry Entry, TreemapRect Rect);
