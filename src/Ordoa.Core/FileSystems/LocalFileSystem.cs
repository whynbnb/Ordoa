using Ordoa.Core.Localization;
using System.Runtime.InteropServices;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.FileSystems;

/// <summary>The native filesystem of the host (Linux first, also macOS and Windows).</summary>
public sealed class LocalFileSystem : FileSystemBase
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly char[] PathSeparators = ['/', '\\'];

    public static LocalFileSystem Instance { get; } = new();

    private LocalFileSystem() : base("local", "file", Environment.MachineName)
    {
        DisplayName = L10n.T("本机");
    }

    public override bool IsLocal => true;
    public override bool IsCaseSensitive => !IsWindows && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public override FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.List | FileSystemCapabilities.Read | FileSystemCapabilities.Write |
        FileSystemCapabilities.CreateDirectory | FileSystemCapabilities.Delete | FileSystemCapabilities.Rename |
        FileSystemCapabilities.Move | FileSystemCapabilities.Copy | FileSystemCapabilities.Symlinks |
        FileSystemCapabilities.UnixPermissions | FileSystemCapabilities.Trash | FileSystemCapabilities.SpaceInfo |
        FileSystemCapabilities.SetModifiedTime;

    public override string RootPath => IsWindows
        ? Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "C:\\"
        : "/";

    public override string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return RootPath;

        if (path == "~")
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (path.StartsWith("~/", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);

        path = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (full.Length > 1 && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length == 0)
                full = RootPath;
            return full;
        }
        catch
        {
            return path;
        }
    }

    public override string Combine(string directory, string name) => NormalizePath(Path.Combine(directory, name));

    public override string GetParent(string path)
    {
        path = NormalizePath(path);
        if (IsRoot(path))
            return path;
        var parent = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(parent) ? RootPath : NormalizePath(parent);
    }

    public override string GetName(string path)
    {
        path = NormalizePath(path);
        if (IsRoot(path))
            return path;
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    public override bool IsRoot(string path)
    {
        path = NormalizePath(path);
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar),
            IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public override string GetDisplayPath(string path) => NormalizePath(path);

    public override Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        return Task.Run<IReadOnlyList<FileEntry>>(() =>
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
                throw new DirectoryNotFoundException(L10n.Format("目录不存在: {0}", path));

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                MatchType = MatchType.Simple,
            };

            var result = new List<FileEntry>();
            foreach (var info in directory.EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    result.Add(BuildEntry(info));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The item vanished or is not accessible; skip it instead of failing the whole listing.
                }
            }

            return result;
        }, cancellationToken);
    }

    public override Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        return Task.Run<FileEntry?>(() =>
        {
            if (IsWindows)
            {
                if (Directory.Exists(path))
                    return BuildEntry(new DirectoryInfo(path));
                if (File.Exists(path))
                    return BuildEntry(new FileInfo(path));
                return null;
            }

            // File.Exists/Directory.Exists follow symlinks and return false for dangling links.
            var attrs = TryGetAttributes(path);
            if (attrs is null)
                return null;

            FileSystemInfo info = (attrs.Value & FileAttributes.Directory) != 0
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            return BuildEntry(info);
        }, cancellationToken);
    }

    public override Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        return Task.FromResult(TryGetAttributes(path) is not null);
    }

    public override Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public override Task<Stream> OpenWriteAsync(string path, bool overwrite, long? expectedLength = null,
        CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        var parent = GetParent(path);
        if (!Directory.Exists(parent))
            Directory.CreateDirectory(parent);

        Stream stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public override Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);

        if (GetAttributesOrNull(path) is { } attrs && (attrs & FileAttributes.ReparsePoint) != 0)
        {
            // Never recurse through a symlink; remove the link itself.
            File.Delete(path);
            return Task.CompletedTask;
        }

        if (Directory.Exists(path))
            Directory.Delete(path, recursive);
        else if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public override Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        sourcePath = NormalizePath(sourcePath);
        destinationPath = NormalizePath(destinationPath);

        var attrs = GetAttributesOrNull(sourcePath);
        if (attrs is { } a && (a & FileAttributes.ReparsePoint) != 0)
        {
            var isDirLink = (a & FileAttributes.Directory) != 0;
            if (isDirLink)
                Directory.Move(sourcePath, destinationPath);
            else
                File.Move(sourcePath, destinationPath);
            return Task.CompletedTask;
        }

        if (Directory.Exists(sourcePath))
            Directory.Move(sourcePath, destinationPath);
        else
            File.Move(sourcePath, destinationPath);
        return Task.CompletedTask;
    }

    public override async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        sourcePath = NormalizePath(sourcePath);
        destinationPath = NormalizePath(destinationPath);

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await CopyStreamAsync(source, destination, progress, cancellationToken).ConfigureAwait(false);

        try
        {
            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
            if (!IsWindows)
                File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
        }
        catch
        {
            // Metadata preservation is best effort.
        }
    }

    public override Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken = default)
    {
        File.SetLastWriteTimeUtc(NormalizePath(path), modified.UtcDateTime);
        return Task.CompletedTask;
    }

    public override Task SetUnixModeAsync(string path, UnixFileMode mode, CancellationToken cancellationToken = default)
    {
        if (!IsWindows)
            File.SetUnixFileMode(NormalizePath(path), mode);
        return Task.CompletedTask;
    }

    public override Task<SpaceInfo> GetSpaceAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                var root = Path.GetPathRoot(NormalizePath(path));
                if (string.IsNullOrEmpty(root))
                    return SpaceInfo.Unknown;
                var drive = new DriveInfo(root);
                return drive.IsReady ? new SpaceInfo(drive.AvailableFreeSpace, drive.TotalSize) : SpaceInfo.Unknown;
            }
            catch
            {
                return SpaceInfo.Unknown;
            }
        }, cancellationToken);
    }

    public static bool IsHidden(string name, FileAttributes attributes)
        => (!IsWindows && name.StartsWith('.')) || (attributes & FileAttributes.Hidden) != 0;

    public static string ExpandUser(string path)
    {
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static FileAttributes? GetAttributesOrNull(string path) => TryGetAttributes(path);

    private static FileEntry BuildEntry(FileSystemInfo info)
    {
        var attrs = info.Attributes;
        var isDirectory = (attrs & FileAttributes.Directory) != 0;
        var isSymlink = (attrs & FileAttributes.ReparsePoint) != 0;
        var name = info.Name;

        string? linkTarget = null;
        if (isSymlink)
        {
            try { linkTarget = info.LinkTarget; }
            catch { /* ignore */ }
        }

        long size = 0;
        if (!isDirectory)
        {
            try { size = info is FileInfo file ? file.Length : 0; }
            catch { size = 0; }
        }

        UnixFileMode? mode = null;
        if (!IsWindows)
        {
            try { mode = File.GetUnixFileMode(info.FullName); }
            catch { /* ignore */ }
        }

        var (owner, group) = UnixIdentity.Read(info.FullName);

        DateTimeOffset? created = SafeTime(() => info.CreationTimeUtc);
        DateTimeOffset? modified = SafeTime(() => info.LastWriteTimeUtc);
        DateTimeOffset? accessed = SafeTime(() => info.LastAccessTimeUtc);

        return new FileEntry
        {
            Name = name,
            Path = info.FullName,
            Kind = isDirectory ? EntryKind.Directory : EntryKind.File,
            Size = size,
            Modified = modified,
            Created = created,
            Accessed = accessed,
            IsHidden = IsHidden(name, attrs),
            IsReadOnly = (attrs & FileAttributes.ReadOnly) != 0,
            IsSymlink = isSymlink,
            LinkTarget = linkTarget,
            UnixMode = mode,
            Owner = owner,
            Group = group,
            MimeType = isDirectory ? null : MimeTypes.FromExtension(GetExtension(name)),
        };
    }

    private static DateTimeOffset? SafeTime(Func<DateTime> getter)
    {
        try
        {
            var value = getter();
            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }
        catch
        {
            return null;
        }
    }

    private static string GetExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..];
    }
}
