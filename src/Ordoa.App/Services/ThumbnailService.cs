using Avalonia.Media.Imaging;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

/// <summary>Bounded, concurrency-limited thumbnail decoder for local images.</summary>
public sealed class ThumbnailService
{
    private const int MaxEntries = 320;
    private readonly SemaphoreSlim _gate = new(3, 3);
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();

    public async Task<Bitmap?> GetAsync(IFileSystem fileSystem, FileEntry entry, int width,
        CancellationToken cancellationToken = default)
    {
        if (!fileSystem.IsLocal || entry.IsDirectory || entry.IsSymlink || entry.Size <= 0 ||
            !MimeTypes.IsImage(entry.Extension) || entry.Size > 200L * 1024 * 1024)
            return null;

        var key = $"{entry.Path}|{entry.Modified?.UtcTicks ?? 0}|{width}";
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Bitmap;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_cache.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value.Bitmap;
                }
            }

            Bitmap? bitmap = null;
            try
            {
                await using var stream = await fileSystem.OpenReadAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                bitmap = Bitmap.DecodeToWidth(stream, Math.Clamp(width, 16, 512));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"缩略图解码失败: {entry.Path}", ex);
            }

            if (bitmap is null)
                return null;

            lock (_sync)
            {
                var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, bitmap));
                _lru.AddFirst(node);
                _cache[key] = node;

                while (_cache.Count > MaxEntries && _lru.Last is { } last)
                {
                    _lru.RemoveLast();
                    _cache.Remove(last.Value.Key);
                    // Do not dispose here: a visible item may still reference this bitmap.
                    // The bitmap becomes collectible once its item view model is gone.
                }
            }

            return bitmap;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            foreach (var entry in _lru)
                entry.Bitmap.Dispose();
            _lru.Clear();
            _cache.Clear();
        }
    }

    private sealed record CacheEntry(string Key, Bitmap Bitmap);
}
