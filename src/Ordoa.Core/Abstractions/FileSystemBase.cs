using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Abstractions;

public abstract class FileSystemBase : IFileSystem
{
    private const int CopyBufferSize = 256 * 1024;

    protected FileSystemBase(string id, string scheme, string displayName)
    {
        Id = id;
        Scheme = scheme;
        DisplayName = displayName;
    }

    public string Id { get; }
    public string Scheme { get; }
    public string DisplayName { get; protected set; }
    public virtual bool IsLocal => false;
    public virtual bool IsCaseSensitive => true;
    public abstract FileSystemCapabilities Capabilities { get; }
    public virtual string RootPath => "/";

    public virtual string NormalizePath(string path) => PosixPath.Normalize(path);
    public virtual string Combine(string directory, string name) => PosixPath.Combine(directory, name);
    public virtual string GetParent(string path) => PosixPath.Parent(path);
    public virtual string GetName(string path) => PosixPath.Name(path);
    public virtual bool IsRoot(string path) => PosixPath.IsRoot(path);
    public virtual string GetDisplayPath(string path) => NormalizePath(path);

    public abstract Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default);

    public virtual async Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        if (IsRoot(path))
        {
            return new FileEntry
            {
                Name = DisplayName,
                Path = path,
                Kind = EntryKind.Directory,
                IsHidden = false,
                Modified = null,
            };
        }

        var parent = GetParent(path);
        var name = GetName(path);
        var entries = await ListAsync(parent, cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(e => string.Equals(e.Name, name, IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
    }

    public virtual async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is not null;

    public abstract Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);
    public abstract Task<Stream> OpenWriteAsync(string path, bool overwrite, long? expectedLength = null, CancellationToken cancellationToken = default);
    public abstract Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    public abstract Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default);
    public abstract Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    public virtual async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var source = await OpenReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        await using var destination = await OpenWriteAsync(destinationPath, overwrite,
            source.CanSeek ? source.Length : null, cancellationToken).ConfigureAwait(false);

        await CopyStreamAsync(source, destination, progress, cancellationToken).ConfigureAwait(false);
    }

    public static async Task CopyStreamAsync(Stream source, Stream destination, IProgress<long>? progress,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[CopyBufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            progress?.Report(total);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual Task SetUnixModeAsync(string path, UnixFileMode mode, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual Task<SpaceInfo> GetSpaceAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(SpaceInfo.Unknown);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected void RequireCapability(FileSystemCapabilities capability, string operation)
    {
        if (!Capabilities.Has(capability))
            throw new NotSupportedException(L10n.Format("{0} 不支持{1}", DisplayName, operation));
    }
}
