using Avalonia.Media.Imaging;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

/// <summary>Bounded, concurrency-limited thumbnail decoder for images, PDFs and videos.</summary>
public sealed class ThumbnailService
{
    private const int MaxEntries = 320;
    private readonly SemaphoreSlim _gate = new(3, 3);
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Func<string?>? _ffmpegPath;

    public ThumbnailService(Func<string?>? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath;
    }

    public static bool CanThumbnail(FileEntry entry, bool isLocal)
    {
        if (entry.IsDirectory || entry.IsSymlink || entry.Size <= 0)
            return false;

        if (MimeTypes.IsImage(entry.Extension))
            return true;
        if (!isLocal)
            return false;

        return entry.Extension.Equals("pdf", StringComparison.OrdinalIgnoreCase) ||
               IsVideo(entry.Extension);
    }

    public async Task<Bitmap?> GetAsync(IFileSystem fileSystem, FileEntry entry, int width,
        CancellationToken cancellationToken = default)
    {
        var maxBytes = fileSystem.IsLocal ? 200L * 1024 * 1024 : 24L * 1024 * 1024;
        if (entry.IsDirectory || entry.IsSymlink || entry.Size <= 0 || entry.Size > maxBytes ||
            !CanThumbnail(entry, fileSystem.IsLocal))
        {
            return null;
        }

        var key = $"{fileSystem.Id}|{entry.Path}|{entry.Modified?.UtcTicks ?? 0}|{width}";
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
                bitmap = await DecodeAsync(fileSystem, entry, width, cancellationToken).ConfigureAwait(false);
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

    private async Task<Bitmap?> DecodeAsync(IFileSystem fileSystem, FileEntry entry, int width,
        CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(width, 16, 512);
        if (MimeTypes.IsImage(entry.Extension))
        {
            await using var stream = await fileSystem.OpenReadAsync(entry.Path, cancellationToken)
                .ConfigureAwait(false);
            return Bitmap.DecodeToWidth(stream, clamped);
        }

        if (entry.Extension.Equals("pdf", StringComparison.OrdinalIgnoreCase) && fileSystem.IsLocal)
        {
            var bytes = await PdfPreviewTool.RenderPageAsync(entry.Path, 1, clamped * 2, cancellationToken)
                .ConfigureAwait(false);
            return bytes is { Length: > 0 } ? DecodeBytes(bytes, clamped) : null;
        }

        if (IsVideo(entry.Extension) && fileSystem.IsLocal)
        {
            var bytes = await FfmpegTool.ExtractFrameAsync(entry.Path, _ffmpegPath?.Invoke(), 1,
                cancellationToken).ConfigureAwait(false);
            return bytes is { Length: > 0 } ? DecodeBytes(bytes, clamped) : null;
        }

        return null;
    }

    private static Bitmap? DecodeBytes(byte[] bytes, int width)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsVideo(string extension) => extension.ToLowerInvariant() is
        "mp4" or "mkv" or "webm" or "avi" or "mov" or "wmv" or "flv" or "m4v" or "mpg" or "mpeg";

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
