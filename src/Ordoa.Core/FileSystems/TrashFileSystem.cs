using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.Core.FileSystems;

/// <summary>Read-only virtual view over the XDG trash. Restore/empty is provided by TrashService.</summary>
public sealed class TrashFileSystem : FileSystemBase
{
    private readonly TrashService _trash;

    public TrashFileSystem(TrashService trash) : base("trash", "trash", "回收站")
    {
        _trash = trash;
    }

    public override FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.List | FileSystemCapabilities.Read | FileSystemCapabilities.Delete;

    public TrashService Trash => _trash;

    public override bool IsRoot(string path) => true;

    public override string GetDisplayPath(string path) => "回收站";

    public override async Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        var entries = await _trash.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        return entries.Select(e => e.Entry).ToList();
    }

    public override async Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default)
    {
        var entries = await _trash.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(e => string.Equals(e.TrashPath, path, StringComparison.Ordinal))?.Entry;
    }

    public override async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is not null;

    public override Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

    public override Task<Stream> OpenWriteAsync(string path, bool overwrite, long? expectedLength = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("回收站不支持写入");

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("回收站不支持新建文件夹");

    public override async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
        => await _trash.DeletePermanentlyAsync(path, cancellationToken).ConfigureAwait(false);

    public override Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("回收站不支持移动");

    public override Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override Task SetUnixModeAsync(string path, UnixFileMode mode, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
