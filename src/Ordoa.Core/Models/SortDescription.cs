namespace Ordoa.Core.Models;

/// <summary>Columns the file list can be ordered by.</summary>
public enum SortColumn
{
    Name,
    Size,
    Modified,
    Type,
    Permissions,
    Owner,
}

public enum ViewKind
{
    List,
    Grid,
    Tree,
    Miller,
}

public sealed class SortDescription
{
    public SortColumn Column { get; set; } = SortColumn.Name;
    public bool Descending { get; set; }
    public bool FoldersFirst { get; set; } = true;
}

/// <summary>A navigation target that can be handed to the UI layer.</summary>
public sealed record NavigationTarget(string FileSystemId, string Path);
