using Ordoa.Core.Abstractions;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public sealed record Bookmark(string Id, string Name, string FileSystemId, string Path, DateTimeOffset AddedAt);

public sealed class BookmarkService
{
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(AppPaths.ConfigDirectory, "bookmarks.json");
    private List<Bookmark> _bookmarks;

    public BookmarkService()
    {
        _bookmarks = JsonFile.Load<List<Bookmark>>(_path) ?? [];
        _bookmarks.RemoveAll(b => string.IsNullOrWhiteSpace(b.Path) || string.IsNullOrWhiteSpace(b.FileSystemId));
    }

    public IReadOnlyList<Bookmark> GetAll()
    {
        lock (_sync)
            return _bookmarks.OrderByDescending(b => b.AddedAt).ToList();
    }

    public bool Contains(string fileSystemId, string path)
    {
        lock (_sync)
            return _bookmarks.Any(b => b.FileSystemId == fileSystemId && string.Equals(b.Path, path, StringComparison.Ordinal));
    }

    public Bookmark Add(IFileSystem fileSystem, string path, string name)
    {
        lock (_sync)
        {
            _bookmarks.RemoveAll(b => b.FileSystemId == fileSystem.Id && string.Equals(b.Path, path, StringComparison.Ordinal));
            var bookmark = new Bookmark(Guid.NewGuid().ToString("N"), name, fileSystem.Id, path, DateTimeOffset.Now);
            _bookmarks.Insert(0, bookmark);
            Persist();
            return bookmark;
        }
    }

    public void Remove(string id)
    {
        lock (_sync)
        {
            _bookmarks.RemoveAll(b => b.Id == id);
            Persist();
        }
    }

    private void Persist()
    {
        try
        {
            JsonFile.Save(_path, _bookmarks);
        }
        catch (Exception ex)
        {
            Log.Warn("保存书签失败", ex);
        }
    }
}
