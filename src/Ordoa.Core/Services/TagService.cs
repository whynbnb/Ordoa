using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

/// <summary>
/// Cross-platform file color/tag store. A sidecar JSON file keeps metadata for
/// local, remote and archive paths without altering the files themselves.
/// </summary>
public sealed class TagService
{
    private readonly object _sync = new();
    private readonly string _storePath;
    private Dictionary<string, FileTagInfo>? _entries;

    public TagService(string? storePath = null)
        => _storePath = storePath ?? Path.Combine(AppPaths.DataDirectory, "tags.json");

    public event EventHandler? Changed;

    public FileTagInfo? Get(IFileSystem fileSystem, string path)
    {
        var key = Key(fileSystem, path);
        lock (_sync)
            return Load().TryGetValue(key, out var info) ? info.Clone() : null;
    }

    public void SetColor(IFileSystem fileSystem, string path, FileColor color)
    {
        Update(fileSystem, path, info => info.Color = color);
    }

    public void SetTags(IFileSystem fileSystem, string path, IEnumerable<string> tags)
    {
        var cleaned = tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
        Update(fileSystem, path, info => info.Tags = cleaned);
    }

    public void Clear(IFileSystem fileSystem, string path)
    {
        var key = Key(fileSystem, path);
        lock (_sync)
        {
            if (!Load().Remove(key))
                return;
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> AllTags()
    {
        lock (_sync)
            return Load().Values
                .SelectMany(info => info.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }

    public static string Key(IFileSystem fileSystem, string path)
        => fileSystem.Id + "|" + fileSystem.NormalizePath(path);

    private void Update(IFileSystem fileSystem, string path, Action<FileTagInfo> apply)
    {
        var key = Key(fileSystem, path);
        lock (_sync)
        {
            var entries = Load();
            if (!entries.TryGetValue(key, out var info))
                entries[key] = info = new FileTagInfo();
            apply(info);
            if (info.IsEmpty)
                entries.Remove(key);
            else
                entries[key] = info;
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, FileTagInfo> Load()
    {
        if (_entries is not null)
            return _entries;

        try
        {
            _entries = JsonFile.Load<Dictionary<string, FileTagInfo>>(_storePath) ?? [];
        }
        catch
        {
            _entries = [];
        }

        foreach (var key in _entries.Where(pair => pair.Value is null || pair.Value.IsEmpty)
                     .Select(pair => pair.Key).ToList())
            _entries.Remove(key);
        return _entries;
    }

    private void Persist()
    {
        try
        {
            JsonFile.Save(_storePath, _entries ?? []);
        }
        catch (Exception ex)
        {
            Log.Warn("保存文件标签失败", ex);
        }
    }
}
