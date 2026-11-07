using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.FileSystems;

/// <summary>
/// Read-only virtual filesystem backed by a zip, tar or tar.gz archive.
/// The archive index is built once; file data is extracted to a temporary
/// cache only when it is actually opened.
/// </summary>
public sealed class ArchiveFileSystem : FileSystemBase
{
    private readonly Dictionary<string, ArchiveNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _entryNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _extracted = new(StringComparer.Ordinal);
    private readonly object _extractLock = new();
    private readonly string _tempRoot;
    private bool _disposed;

    private ArchiveFileSystem(string archivePath, ArchiveFormat format)
        : base("archive:" + HashPath(archivePath), "archive", Path.GetFileName(archivePath))
    {
        ArchivePath = archivePath;
        Format = format;
        _tempRoot = Path.Combine(AppPaths.CacheDirectory, "archives",
            HashPath(archivePath)[..16] + "-" + Guid.NewGuid().ToString("N")[..8]);
        _nodes["/"] = new ArchiveNode("/", DisplayName, true, 0, null);
    }

    public string ArchivePath { get; }
    public ArchiveFormat Format { get; }
    public override bool IsCaseSensitive => true;
    public override FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.List | FileSystemCapabilities.Read | FileSystemCapabilities.Copy;

    public override string GetDisplayPath(string path) => $"{DisplayName}:{NormalizePath(path)}";

    /// <summary>Removes extraction folders left behind by older sessions.</summary>
    public static void CleanupStaleCache(TimeSpan? olderThan = null)
    {
        try
        {
            var root = Path.Combine(AppPaths.CacheDirectory, "archives");
            if (!Directory.Exists(root))
                return;
            var cutoff = DateTime.UtcNow - (olderThan ?? TimeSpan.FromDays(1));
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(directory) < cutoff)
                        Directory.Delete(directory, true);
                }
                catch
                {
                    // Another instance may still be reading this archive.
                }
            }
        }
        catch
        {
            // Cache cleanup is best effort.
        }
    }

    public static Task<ArchiveFileSystem> OpenAsync(string archivePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var format = DetectFormat(archivePath);
            if (format == ArchiveFormat.None)
                throw new NotSupportedException(L10n.T("仅支持 zip、tar 和 tar.gz 格式"));

            var fileSystem = new ArchiveFileSystem(Path.GetFullPath(archivePath), format);
            fileSystem.BuildIndex(cancellationToken);
            return fileSystem;
        }, cancellationToken);
    }

    public override Task<IReadOnlyList<FileEntry>> ListAsync(string path,
        CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        if (!_nodes.TryGetValue(path, out var node) || !node.IsDirectory)
            throw new DirectoryNotFoundException(L10n.Format("目录不存在: {0}", path));

        var result = _nodes.Values
            .Where(candidate => !string.Equals(candidate.Path, path, StringComparison.Ordinal) &&
                                string.Equals(PosixPath.Parent(candidate.Path), path, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.IsDirectory)
            .ThenBy(candidate => candidate.Name, NaturalStringComparer.Instance)
            .Select(candidate => ToEntry(candidate))
            .ToList();
        return Task.FromResult<IReadOnlyList<FileEntry>>(result);
    }

    public override Task<FileEntry?> GetEntryAsync(string path,
        CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        return Task.FromResult(_nodes.TryGetValue(path, out var node) ? ToEntry(node) : null);
    }

    public override Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(_nodes.ContainsKey(NormalizePath(path)));

    public override Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        if (!_nodes.TryGetValue(path, out var node) || node.IsDirectory)
            throw new FileNotFoundException(L10n.T("文件不存在"), path);

        lock (_extractLock)
        {
            if (_extracted.TryGetValue(path, out var cached) && File.Exists(cached))
                return Task.FromResult<Stream>(new FileStream(cached, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 128 * 1024, FileOptions.SequentialScan));

            Directory.CreateDirectory(_tempRoot);
            var tempPath = Path.Combine(_tempRoot, HashPath(path) + ".bin");
            ExtractEntry(path, tempPath, cancellationToken);
            _extracted[path] = tempPath;
            return Task.FromResult<Stream>(new FileStream(tempPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 128 * 1024, FileOptions.SequentialScan));
        }
    }

    public override Task<Stream> OpenWriteAsync(string path, bool overwrite, long? expectedLength = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(L10n.T("压缩包是只读的"));

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(L10n.T("压缩包是只读的"));

    public override Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(L10n.T("压缩包是只读的"));

    public override Task MoveAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(L10n.T("压缩包是只读的"));

    public override Task SetModifiedAsync(string path, DateTimeOffset modified,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(L10n.T("压缩包是只读的"));

    public override Task SetUnixModeAsync(string path, UnixFileMode mode,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(L10n.T("压缩包是只读的"));

    public override ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, true);
        }
        catch
        {
            // Temporary files can be cleaned up on the next launch.
        }
        return ValueTask.CompletedTask;
    }

    private void BuildIndex(CancellationToken cancellationToken)
    {
        if (Format == ArchiveFormat.Zip)
            BuildZipIndex(cancellationToken);
        else
            BuildTarIndex(cancellationToken);

        foreach (var node in _nodes.Values.ToList())
        {
            if (node.Path == "/")
                continue;
            var parent = PosixPath.Parent(node.Path);
            while (parent != "/" && !_nodes.ContainsKey(parent))
            {
                _nodes[parent] = new ArchiveNode(parent, PosixPath.Name(parent), true, 0, null);
                parent = PosixPath.Parent(parent);
            }
        }
    }

    private void BuildZipIndex(CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(ArchivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NormalizeEntryName(entry.FullName);
            if (name.Length == 0 || name == "/")
                continue;

            var isDirectory = string.IsNullOrEmpty(entry.Name) ||
                              entry.FullName.EndsWith('/') ||
                              entry.FullName.EndsWith('\\');
            AddNode(name, isDirectory, isDirectory ? 0 : entry.Length, entry.LastWriteTime);
            if (!isDirectory)
                _entryNames[name] = entry.FullName;
        }
    }

    private void BuildTarIndex(CancellationToken cancellationToken)
    {
        using var file = new FileStream(ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream source = Format == ArchiveFormat.TarGz
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true)
            : file;
        try
        {
            using var reader = new TarReader(source, leaveOpen: true);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = NormalizeEntryName(entry.Name);
                if (name.Length == 0 || name == "/")
                    continue;

                var isDirectory = entry.EntryType is TarEntryType.Directory;
                var isFile = entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or
                    TarEntryType.ContiguousFile;
                if (!isDirectory && !isFile)
                    continue;

                AddNode(name, isDirectory, isFile ? entry.Length : 0, entry.ModificationTime);
                if (isFile)
                    _entryNames[name] = entry.Name;
            }
        }
        finally
        {
            if (Format == ArchiveFormat.TarGz)
                source.Dispose();
        }
    }

    private void AddNode(string path, bool isDirectory, long size, DateTimeOffset modified)
    {
        path = PosixPath.Normalize(path);
        if (path == "/")
            return;

        if (!_nodes.TryGetValue(path, out var existing) || !existing.IsDirectory)
            _nodes[path] = new ArchiveNode(path, PosixPath.Name(path), isDirectory, size, modified);

        var parent = PosixPath.Parent(path);
        while (parent != "/" && !_nodes.ContainsKey(parent))
        {
            _nodes[parent] = new ArchiveNode(parent, PosixPath.Name(parent), true, 0, null);
            parent = PosixPath.Parent(parent);
        }
    }

    private void ExtractEntry(string path, string destination, CancellationToken cancellationToken)
    {
        if (!_entryNames.TryGetValue(path, out var entryName))
            throw new FileNotFoundException(L10n.T("文件不存在"), path);

        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            128 * 1024);
        if (Format == ArchiveFormat.Zip)
        {
            using var archive = ZipFile.OpenRead(ArchivePath);
            var entry = archive.GetEntry(entryName)
                ?? archive.Entries.FirstOrDefault(candidate =>
                    string.Equals(candidate.FullName, entryName, StringComparison.Ordinal));
            if (entry is null)
                throw new FileNotFoundException(L10n.T("文件不存在"), path);
            using var input = entry.Open();
            input.CopyTo(output);
            return;
        }

        using var file = new FileStream(ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream source = Format == ArchiveFormat.TarGz
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true)
            : file;
        try
        {
            using var reader = new TarReader(source, leaveOpen: true);
            TarEntry? tarEntry;
            while ((tarEntry = reader.GetNextEntry()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(tarEntry.Name, entryName, StringComparison.Ordinal) ||
                    tarEntry.DataStream is null)
                    continue;
                tarEntry.DataStream.CopyTo(output);
                return;
            }
        }
        finally
        {
            if (Format == ArchiveFormat.TarGz)
                source.Dispose();
        }

        throw new FileNotFoundException(L10n.T("文件不存在"), path);
    }

    private FileEntry ToEntry(ArchiveNode node)
        => new()
        {
            Name = node.Name,
            Path = node.Path,
            Kind = node.IsDirectory ? EntryKind.Directory : EntryKind.File,
            Size = node.Size,
            Modified = node.Modified,
            IsHidden = node.Name.StartsWith('.'),
            MimeType = node.IsDirectory ? null : MimeTypes.FromExtension(GetExtension(node.Name)),
        };

    private static string NormalizeEntryName(string name)
    {
        var normalized = PosixPath.Normalize("/" + name.Replace('\\', '/').TrimStart('/'));
        return normalized;
    }

    private static string GetExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
    }

    private static string HashPath(string path)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();

    private static ArchiveFormat DetectFormat(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".zip", StringComparison.Ordinal))
            return ArchiveFormat.Zip;
        if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
            return ArchiveFormat.TarGz;
        if (lower.EndsWith(".tar", StringComparison.Ordinal))
            return ArchiveFormat.Tar;
        return ArchiveFormat.None;
    }

    private sealed record ArchiveNode(string Path, string Name, bool IsDirectory, long Size,
        DateTimeOffset? Modified);
}

public enum ArchiveFormat
{
    None,
    Zip,
    Tar,
    TarGz,
}
