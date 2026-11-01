using Ordoa.Core.Models;

namespace Ordoa.Core.Abstractions;

/// <summary>
/// A mounted or virtual filesystem. Paths are provider native strings: absolute OS
/// paths for the local filesystem and POSIX paths for remote providers.
/// </summary>
public interface IFileSystem : IAsyncDisposable
{
    string Id { get; }
    string Scheme { get; }
    string DisplayName { get; }
    bool IsLocal { get; }
    bool IsCaseSensitive { get; }
    FileSystemCapabilities Capabilities { get; }
    string RootPath { get; }

    string NormalizePath(string path);
    string Combine(string directory, string name);
    string GetParent(string path);
    string GetName(string path);
    bool IsRoot(string path);

    /// <summary>Path shown in the breadcrumb (includes the connection for remotes).</summary>
    string GetDisplayPath(string path);

    Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default);
    Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);
    Task<Stream> OpenWriteAsync(string path, bool overwrite, long? expectedLength = null, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default);
    Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, IProgress<long>? progress = null, CancellationToken cancellationToken = default);

    Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken = default);
    Task SetUnixModeAsync(string path, UnixFileMode mode, CancellationToken cancellationToken = default);
    Task<SpaceInfo> GetSpaceAsync(string path, CancellationToken cancellationToken = default);
}
