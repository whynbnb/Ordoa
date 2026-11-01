using Ordoa.Core.Abstractions;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public sealed record RecentEntry(string FileSystemId, string Path, string Name, DateTimeOffset VisitedAt)
{
    public string DisplayPath => Path;
}

public sealed class RecentService
{
    private const int MaxEntries = 15;
    private readonly object _sync = new();
    private List<RecentEntry> _entries;
    private DateTime _lastSave = DateTime.MinValue;

    public RecentService()
    {
        _entries = JsonFile.Load<List<RecentEntry>>(AppPaths.RecentFile) ?? [];
        _entries.RemoveAll(e => string.IsNullOrWhiteSpace(e.Path) || string.IsNullOrWhiteSpace(e.FileSystemId));
    }

    public IReadOnlyList<RecentEntry> GetAll()
    {
        lock (_sync)
            return _entries.OrderByDescending(e => e.VisitedAt).ToList();
    }

    public void Add(IFileSystem fileSystem, string path, string displayName)
    {
        if (fileSystem is TrashFileSystem)
            return;

        lock (_sync)
        {
            _entries.RemoveAll(e => e.FileSystemId == fileSystem.Id &&
                                   string.Equals(e.Path, path, StringComparison.Ordinal));
            _entries.Insert(0, new RecentEntry(fileSystem.Id, path, displayName, DateTimeOffset.Now));
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

            // Avoid synchronous disk writes while the user is rapidly navigating.
            if (DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(5))
                return;
            _lastSave = DateTime.UtcNow;
            Persist();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            Persist();
        }
    }

    public void Flush()
    {
        lock (_sync)
            Persist();
    }

    private void Persist()
    {
        try
        {
            JsonFile.Save(AppPaths.RecentFile, _entries);
        }
        catch (Exception ex)
        {
            Log.Warn("保存最近访问记录失败", ex);
        }
    }
}
