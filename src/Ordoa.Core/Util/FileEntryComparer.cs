using Ordoa.Core.Models;

namespace Ordoa.Core.Util;

public sealed class FileEntryComparer : IComparer<FileEntry>
{
    private readonly SortColumn _column;
    private readonly bool _descending;
    private readonly bool _foldersFirst;

    public FileEntryComparer(SortColumn column, bool descending = false, bool foldersFirst = true)
    {
        _column = column;
        _descending = descending;
        _foldersFirst = foldersFirst;
    }

    public int Compare(FileEntry? x, FileEntry? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        if (_foldersFirst)
        {
            var folderResult = (y.IsDirectory ? 1 : 0) - (x.IsDirectory ? 1 : 0);
            if (folderResult != 0)
                return folderResult;
        }

        var result = _column switch
        {
            SortColumn.Name => NaturalStringComparer.Instance.Compare(x.Name, y.Name),
            SortColumn.Size => x.Size.CompareTo(y.Size),
            SortColumn.Modified => Nullable.Compare(x.Modified, y.Modified),
            SortColumn.Type => CompareType(x, y),
            _ => 0,
        };

        if (result == 0)
            result = NaturalStringComparer.Instance.Compare(x.Name, y.Name);

        return _descending ? -result : result;
    }

    private static int CompareType(FileEntry x, FileEntry y)
    {
        var result = string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase);
        return result != 0 ? result : NaturalStringComparer.Instance.Compare(x.Name, y.Name);
    }
}
