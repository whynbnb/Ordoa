using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

/// <summary>
/// Freedesktop.org trash implementation. Supports the home trash and per-volume
/// top-directory trashes (.Trash/$uid and .Trash-$uid).
/// </summary>
public sealed partial class TrashService
{
    private const string InfoHeader = "[Trash Info]";

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public string HomeTrashDirectory => AppPaths.TrashDirectory;

    public sealed record TrashLocation(string Name, string MountPoint, string FilesDirectory, string InfoDirectory);

    public async Task<IReadOnlyList<TrashEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return [];

        var result = new List<TrashEntry>();
        foreach (var location in GetTrashLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(location.FilesDirectory))
                continue;

            foreach (var path in Directory.EnumerateFileSystemEntries(location.FilesDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(path);
                var infoPath = Path.Combine(location.InfoDirectory, fileName + ".trashinfo");
                if (!File.Exists(infoPath))
                    continue;

                try
                {
                    var info = ParseInfo(infoPath);
                    if (info is null)
                        continue;

                    FileSystemInfo infoFs = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                    var isDirectory = infoFs is DirectoryInfo;
                    var entry = new FileEntry
                    {
                        Name = Path.GetFileName(info.Value.OriginalPath.TrimEnd('/')),
                        Path = path,
                        Kind = isDirectory ? EntryKind.Directory : EntryKind.File,
                        Size = isDirectory ? 0 : infoFs is FileInfo fi ? fi.Length : 0,
                        Modified = info.Value.DeletedAt,
                        DeletedAt = info.Value.DeletedAt,
                        OriginalPath = info.Value.OriginalPath,
                        IsHidden = Path.GetFileName(info.Value.OriginalPath).StartsWith('.'),
                        UnixMode = TryGetMode(path),
                    };

                    result.Add(new TrashEntry(entry, location, path, infoPath, info.Value.OriginalPath, info.Value.DeletedAt));
                }
                catch
                {
                    // Skip unreadable/malformed trash entries.
                }
            }
        }

        return result.OrderByDescending(e => e.DeletedAt).ToList();
    }

    public Task<TrashEntry> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            throw new NotSupportedException("当前平台不支持回收站");

        return Task.Run(() =>
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("要删除的项目不存在", path);

            var location = ResolveTrashLocation(path);
            Directory.CreateDirectory(location.FilesDirectory);
            Directory.CreateDirectory(location.InfoDirectory);
            TrySetPrivatePermissions(location.FilesDirectory);
            TrySetPrivatePermissions(location.InfoDirectory);

            var originalName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            var fileName = MakeUniqueName(location.FilesDirectory, originalName);
            var trashPath = Path.Combine(location.FilesDirectory, fileName);
            var infoPath = Path.Combine(location.InfoDirectory, fileName + ".trashinfo");
            var deletedAt = DateTimeOffset.Now;

            var infoText = BuildInfo(path, deletedAt);
            File.WriteAllText(infoPath, infoText, new UTF8Encoding(false));

            try
            {
                if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
                    Directory.Move(path, trashPath);
                else
                    File.Move(path, trashPath);
            }
            catch
            {
                try { File.Delete(infoPath); } catch { /* ignore */ }
                throw;
            }

            var entry = new FileEntry
            {
                Name = originalName,
                Path = trashPath,
                Kind = Directory.Exists(trashPath) ? EntryKind.Directory : EntryKind.File,
                Modified = deletedAt,
                DeletedAt = deletedAt,
                OriginalPath = path,
            };
            return new TrashEntry(entry, location, trashPath, infoPath, path, deletedAt);
        }, cancellationToken);
    }

    public Task<string> RestoreAsync(TrashEntry entry, CancellationToken cancellationToken = default)
        => RestoreAsync(entry.TrashPath, cancellationToken);

    public Task<string> RestoreAsync(string trashPath, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            throw new NotSupportedException("当前平台不支持回收站");

        return Task.Run(() =>
        {
            var infoPath = GetInfoPathForTrashFile(trashPath);
            var info = ParseInfo(infoPath) ?? throw new InvalidDataException("无法读取回收站信息文件");
            var destination = info.OriginalPath;

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            if (File.Exists(destination) || Directory.Exists(destination))
            {
                var unique = GlobMatcher.SuggestFileName(Path.GetFileName(destination),
                    name => File.Exists(Path.Combine(parent ?? "/", name)) || Directory.Exists(Path.Combine(parent ?? "/", name)));
                destination = Path.Combine(parent ?? "/", unique);
            }

            if (Directory.Exists(trashPath) && new DirectoryInfo(trashPath).LinkTarget is null)
                Directory.Move(trashPath, destination);
            else
                File.Move(trashPath, destination);

            try { File.Delete(infoPath); } catch { /* ignore */ }
            return destination;
        }, cancellationToken);
    }

    public Task DeletePermanentlyAsync(string trashPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (Directory.Exists(trashPath) && new DirectoryInfo(trashPath).LinkTarget is null)
                Directory.Delete(trashPath, true);
            else if (File.Exists(trashPath))
                File.Delete(trashPath);

            var infoPath = GetInfoPathForTrashFile(trashPath);
            try { File.Delete(infoPath); } catch { /* ignore */ }
        }, cancellationToken);
    }

    public Task EmptyAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            foreach (var location in GetTrashLocations())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(location.FilesDirectory))
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(location.FilesDirectory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
                                Directory.Delete(path, true);
                            else
                                File.Delete(path);
                        }
                        catch { /* keep emptying */ }
                    }
                }

                if (Directory.Exists(location.InfoDirectory))
                {
                    foreach (var info in Directory.EnumerateFiles(location.InfoDirectory, "*.trashinfo"))
                    {
                        try { File.Delete(info); } catch { /* ignore */ }
                    }
                }
            }
        }, cancellationToken);
    }

    public string GetInfoPathForTrashFile(string trashPath)
    {
        var filesDir = Path.GetDirectoryName(trashPath) ?? string.Empty;
        var name = Path.GetFileName(trashPath);
        var parent = Path.GetDirectoryName(filesDir);
        return Path.Combine(parent ?? filesDir, "info", name + ".trashinfo");
    }

    public IReadOnlyList<TrashLocation> GetTrashLocations()
    {
        var locations = new List<TrashLocation>
        {
            new("主目录", AppPaths.Home, Path.Combine(HomeTrashDirectory, "files"), Path.Combine(HomeTrashDirectory, "info")),
        };

        if (!IsSupported)
            return locations;

        var uid = GetUid();
        foreach (var mount in GetMountPoints())
        {
            if (mount.Value == AppPaths.Home || mount.Value == "/")
                continue;

            var mountPoint = mount.Value;
            var shared = Path.Combine(mountPoint, ".Trash");
            if (Directory.Exists(shared))
            {
                var userDir = Path.Combine(shared, uid.ToString(CultureInfo.InvariantCulture));
                if (Directory.Exists(userDir) && IsWritable(userDir))
                    locations.Add(new TrashLocation(Path.GetFileName(mountPoint.TrimEnd('/')), mountPoint,
                        Path.Combine(userDir, "files"), Path.Combine(userDir, "info")));
            }

            var privateDir = Path.Combine(mountPoint, $".Trash-{uid}");
            if (Directory.Exists(privateDir) && IsWritable(privateDir))
                locations.Add(new TrashLocation(Path.GetFileName(mountPoint.TrimEnd('/')), mountPoint,
                    Path.Combine(privateDir, "files"), Path.Combine(privateDir, "info")));
        }

        return locations.DistinctBy(l => l.FilesDirectory).ToList();
    }

    private TrashLocation ResolveTrashLocation(string path)
    {
        var uid = GetUid();
        var mounts = GetMountPoints();
        var targetMount = LongestPrefix(mounts, path);
        var homeMount = LongestPrefix(mounts, AppPaths.Home);

        if (string.Equals(targetMount, homeMount, StringComparison.Ordinal))
            return new TrashLocation("主目录", AppPaths.Home,
                Path.Combine(HomeTrashDirectory, "files"), Path.Combine(HomeTrashDirectory, "info"));

        if (!string.IsNullOrEmpty(targetMount) && targetMount != "/")
        {
            var shared = Path.Combine(targetMount, ".Trash");
            if (Directory.Exists(shared) && IsWritable(shared))
            {
                var userDir = Path.Combine(shared, uid.ToString(CultureInfo.InvariantCulture));
                if (!Directory.Exists(userDir))
                    Directory.CreateDirectory(userDir);
                TrySetPrivatePermissions(userDir);
                if (IsWritable(userDir))
                    return new TrashLocation(Path.GetFileName(targetMount.TrimEnd('/')), targetMount,
                        Path.Combine(userDir, "files"), Path.Combine(userDir, "info"));
            }

            var privateDir = Path.Combine(targetMount, $".Trash-{uid}");
            if (!Directory.Exists(privateDir))
                Directory.CreateDirectory(privateDir);
            TrySetPrivatePermissions(privateDir);
            if (IsWritable(privateDir))
                return new TrashLocation(Path.GetFileName(targetMount.TrimEnd('/')), targetMount,
                    Path.Combine(privateDir, "files"), Path.Combine(privateDir, "info"));
        }

        return new TrashLocation("主目录", AppPaths.Home,
            Path.Combine(HomeTrashDirectory, "files"), Path.Combine(HomeTrashDirectory, "info"));
    }

    private static List<KeyValuePair<int, string>> GetMountPoints()
    {
        var result = new List<KeyValuePair<int, string>>();
        if (!File.Exists("/proc/self/mounts"))
            return result;

        foreach (var line in File.ReadLines("/proc/self/mounts"))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            var mountPoint = Unescape(parts[1]);
            result.Add(new KeyValuePair<int, string>(mountPoint.Length, mountPoint));
        }
        return result;
    }

    private static string LongestPrefix(List<KeyValuePair<int, string>> mounts, string path)
    {
        var best = "/";
        foreach (var mount in mounts)
        {
            var m = mount.Value;
            if (path.Equals(m, StringComparison.Ordinal) ||
                path.StartsWith(m.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                if (m.Length > best.Length)
                    best = m;
            }
        }
        return best;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length &&
                value[i + 1] is >= '0' and <= '7' && value[i + 2] is >= '0' and <= '7' && value[i + 3] is >= '0' and <= '7')
            {
                builder.Append((char)((value[i + 1] - '0') * 64 + (value[i + 2] - '0') * 8 + (value[i + 3] - '0')));
                i += 3;
            }
            else
            {
                builder.Append(value[i]);
            }
        }
        return builder.ToString();
    }

    private static string BuildInfo(string originalPath, DateTimeOffset deletedAt)
    {
        var escaped = EscapePath(originalPath);
        var date = deletedAt.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return $"{InfoHeader}\nPath={escaped}\nDeletionDate={date}\n";
    }

    public static string EscapePath(string path)
    {
        var builder = new StringBuilder(path.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~' or '+' or ':')
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static (string OriginalPath, DateTimeOffset DeletedAt)? ParseInfo(string infoPath)
    {
        if (!File.Exists(infoPath))
            return null;

        var lines = File.ReadAllLines(infoPath);
        string? rawPath = null;
        string? rawDate = null;
        var inSection = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == InfoHeader)
            {
                inSection = true;
                continue;
            }
            if (!inSection || trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var index = trimmed.IndexOf('=');
            if (index <= 0)
                continue;
            var key = trimmed[..index];
            var value = trimmed[(index + 1)..];
            if (key.Equals("Path", StringComparison.Ordinal))
                rawPath = value;
            else if (key.Equals("DeletionDate", StringComparison.Ordinal))
                rawDate = value;
        }

        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        string original;
        try { original = Uri.UnescapeDataString(rawPath); }
        catch { original = rawPath; }

        if (!Path.IsPathRooted(original))
            return null;

        var deletedAt = DateTimeOffset.Now;
        if (!string.IsNullOrWhiteSpace(rawDate) &&
            DateTimeOffset.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            deletedAt = parsed;
        }

        return (original, deletedAt);
    }

    private static string MakeUniqueName(string directory, string name)
    {
        var candidate = name;
        if (!File.Exists(Path.Combine(directory, candidate)) && !Directory.Exists(Path.Combine(directory, candidate)))
            return candidate;

        for (var i = 1; i < 10000; i++)
        {
            candidate = i == 1 ? $"{name}.1" : $"{name}.{i}";
            if (!File.Exists(Path.Combine(directory, candidate)) && !Directory.Exists(Path.Combine(directory, candidate)))
                return candidate;
        }
        return name + "." + Guid.NewGuid().ToString("N");
    }

    private static void TrySetPrivatePermissions(string path)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { /* best effort */ }
    }

    private static UnixFileMode? TryGetMode(string path)
    {
        try { return File.GetUnixFileMode(path); }
        catch { return null; }
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".ordoa-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}

public sealed record TrashEntry(
    FileEntry Entry,
    TrashService.TrashLocation Location,
    string TrashPath,
    string InfoPath,
    string OriginalPath,
    DateTimeOffset DeletedAt);
