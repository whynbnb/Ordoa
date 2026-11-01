namespace Ordoa.Core.Services;

public enum TransferMode
{
    Copy,
    Move,
}

/// <summary>
/// Resolves the transfer mode for a drag and drop operation from the keyboard
/// modifiers and the relative location of the source and destination.
/// </summary>
public static class TransferModifiers
{
    /// <summary>
    /// Ctrl always copies, Shift always moves. Without a modifier files are
    /// moved inside the same file system and copied across file systems.
    /// </summary>
    public static TransferMode Resolve(bool control, bool shift, bool sameFileSystem)
    {
        if (control)
            return TransferMode.Copy;
        if (shift)
            return TransferMode.Move;
        return sameFileSystem ? TransferMode.Move : TransferMode.Copy;
    }
}
