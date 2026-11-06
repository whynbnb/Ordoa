namespace Ordoa.Core.Services;

public sealed record FileScanEntry(string Path, long Size);

/// <summary>
/// Safe, reparse-point-free file enumeration shared by the analysis tools.
/// Inaccessible directories are skipped instead of aborting the whole scan.
/// </summary>
public static class FileScanWalker
{
    public static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static string[] GetFiles(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*", Options);
        }
        catch
        {
            return [];
        }
    }

    public static string[] GetDirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory, "*", Options);
        }
        catch
        {
            return [];
        }
    }

    public static IEnumerable<FileScanEntry> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            foreach (var file in GetFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    continue;
                }

                yield return new FileScanEntry(file, size);
            }

            foreach (var child in GetDirectories(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                pending.Push(child);
            }
        }
    }
}
