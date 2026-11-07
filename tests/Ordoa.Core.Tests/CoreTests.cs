using Ordoa.Core.FileSystems;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

public sealed class PathAndFormatTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("a/b", "a/b")]
    public void Normalize_Works(string input, string expected)
        => Assert.Equal(expected, PosixPath.Normalize(input));

    [Fact]
    public void CombineAndParent_Work()
    {
        Assert.Equal("/a/b", PosixPath.Combine("/a", "b"));
        Assert.Equal("/a", PosixPath.Parent("/a/b"));
        Assert.Equal("/", PosixPath.Parent("/a"));
        Assert.Equal("b", PosixPath.Name("/a/b/"));
        Assert.True(PosixPath.IsRoot("/"));
    }

    [Fact]
    public void NaturalComparer_SortsNumbersNaturally()
    {
        var names = new[] { "file10", "file2", "file1", "File3" };
        var sorted = names.OrderBy(n => n, NaturalStringComparer.Instance).ToArray();
        Assert.Equal(["file1", "file2", "File3", "file10"], sorted);
    }

    [Fact]
    public void Humanize_FormatsSizes()
    {
        Assert.Equal("0 B", Humanize.Size(0));
        Assert.Equal("1.0 KB", Humanize.Size(1024));
        Assert.Equal("1.5 MB", Humanize.Size(1024 * 1024 + 512 * 1024));
    }

    [Fact]
    public void TrashEscape_RoundTrips()
    {
        var path = "/tmp/Ünïcode dir/文件 100%.txt";
        var escaped = TrashService.EscapePath(path);
        Assert.DoesNotContain(' ', escaped);
        Assert.Equal(path, Uri.UnescapeDataString(escaped));
    }

    [Fact]
    public void Volumes_EnumerateWithoutThrowing()
    {
        var volumes = VolumeService.GetVolumes();
        Assert.NotNull(volumes);
    }

    [Fact]
    public void FilePermission_FormatsUnixMode()
    {
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                   UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Assert.Equal("rwxr-xr-x", FilePermission.Format(mode));
        Assert.Equal("—", FilePermission.Format(null));
    }

    [Fact]
    public void UnixIdentity_ReadsCurrentUserOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var path = Path.Combine(Path.GetTempPath(), "ordoa-identity-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "identity");
        try
        {
            var (owner, group) = UnixIdentity.Read(path);
            Assert.False(string.IsNullOrWhiteSpace(owner));
            Assert.False(string.IsNullOrWhiteSpace(group));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GlobMatcher_Works()
    {
        Assert.True(GlobMatcher.IsMatch("photo.png", "*.png"));
        Assert.True(GlobMatcher.IsMatch("a1.txt", "a?.txt"));
        Assert.False(GlobMatcher.IsMatch("a12.txt", "a?.txt"));
        Assert.True(GlobMatcher.IsMatch("README", "read"));
    }
}

public sealed class LocalFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoa-test-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileSystem _fs = LocalFileSystem.Instance;

    public LocalFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ListsFilesAndDirectories()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.SetUnixFileMode(Path.Combine(_root, "a.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var entries = await _fs.ListAsync(_root);
        Assert.Equal(2, entries.Count);
        var file = Assert.Single(entries, e => e.Name == "a.txt");
        Assert.Equal(5, file.Size);
        Assert.False(file.IsDirectory);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, file.UnixMode);
        var dir = Assert.Single(entries, e => e.Name == "sub");
        Assert.True(dir.IsDirectory);
    }

    [Fact]
    public async Task WriteReadMoveDelete_RoundTrip()
    {
        var source = Path.Combine(_root, "source.bin");
        await using (var stream = await _fs.OpenWriteAsync(source, false))
        {
            await stream.WriteAsync("payload"u8.ToArray());
        }

        var text = await File.ReadAllTextAsync(source);
        Assert.Equal("payload", text);

        var dest = Path.Combine(_root, "dest.bin");
        await _fs.CopyFileAsync(source, dest, true);
        Assert.Equal("payload", await File.ReadAllTextAsync(dest));

        var moved = Path.Combine(_root, "moved.bin");
        await _fs.MoveAsync(dest, moved);
        Assert.False(File.Exists(dest));
        Assert.True(File.Exists(moved));

        await _fs.DeleteAsync(moved, false);
        Assert.False(File.Exists(moved));
    }

    [Fact]
    public async Task Symlink_IsReportedAndNotFollowedOnDelete()
    {
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        await File.WriteAllTextAsync(Path.Combine(_root, "target", "file.txt"), "x");
        var link = Path.Combine(_root, "link");
        File.CreateSymbolicLink(link, Path.Combine(_root, "target"));

        var entries = await _fs.ListAsync(_root);
        var entry = Assert.Single(entries, e => e.Name == "link");
        Assert.True(entry.IsSymlink);

        await _fs.DeleteAsync(link, true);
        Assert.False(File.Exists(link));
        Assert.True(Directory.Exists(Path.Combine(_root, "target")));
    }
}

public sealed class FileOperationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoa-ops-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileSystem _fs = LocalFileSystem.Instance;

    public FileOperationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private static async Task<FileOperationItem> WaitAsync(FileOperationService service, Task<FileOperationItem> start)
    {
        var item = await start;
        var timeout = DateTime.UtcNow.AddSeconds(15);
        while (item.State is OperationState.Queued or OperationState.Scanning or OperationState.Running)
        {
            if (DateTime.UtcNow > timeout)
                throw new TimeoutException("Operation did not finish");
            await Task.Delay(25);
        }
        return item;
    }

    [Fact]
    public async Task CopyDirectory_And_ReportSuccess()
    {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "b.txt"), "bb");
        var dest = Path.Combine(_root, "dest");
        Directory.CreateDirectory(dest);

        using var service = new FileOperationService(new TrashService());
        var item = await WaitAsync(service,
            Task.FromResult(service.Copy([Source(source)], _fs, dest, false)));

        Assert.Equal(OperationState.Completed, item.State);
        Assert.True(File.Exists(Path.Combine(dest, "src", "a.txt")));
        Assert.Equal("bb", await File.ReadAllTextAsync(Path.Combine(dest, "src", "nested", "b.txt")));
        var result = Assert.Single(item.Results);
        Assert.Equal(Path.Combine(dest, "src"), result.Destination);
        Assert.True(result.DestinationEntry?.IsDirectory);
    }

    [Fact]
    public async Task CopyFile_ConflictRenames()
    {
        var source = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(source, "source");
        var dest = Path.Combine(_root, "out");
        Directory.CreateDirectory(dest);
        await File.WriteAllTextAsync(Path.Combine(dest, "a.txt"), "existing");

        using var service = new FileOperationService(new TrashService());
        var item = await WaitAsync(service, Task.FromResult(service.Copy([Source(source)], _fs, dest, false)));

        Assert.Equal(OperationState.Completed, item.State);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(dest, "a.txt")));
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(dest, "a (1).txt")));
        Assert.Equal(Path.Combine(dest, "a (1).txt"), Assert.Single(item.Results).Destination);
    }

    [Fact]
    public async Task MoveDirectory_AcrossFolders()
    {
        var source = Path.Combine(_root, "move-src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "x.txt"), "x");
        var dest = Path.Combine(_root, "move-dest");
        Directory.CreateDirectory(dest);

        using var service = new FileOperationService(new TrashService());
        var item = await WaitAsync(service, Task.FromResult(service.Copy([Source(source)], _fs, dest, true)));

        Assert.Equal(OperationState.Completed, item.State);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(dest, "move-src", "x.txt")));
        Assert.Single(item.Results);
    }

    [Fact]
    public async Task CopyDirectoryIntoItself_IsRejected()
    {
        var source = Path.Combine(_root, "loop");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "a");

        using var service = new FileOperationService(new TrashService());
        var item = await WaitAsync(service,
            Task.FromResult(service.Copy([Source(source)], _fs, Path.Combine(source, "sub"), false)));

        Assert.Equal(OperationState.Failed, item.State);
        Assert.False(Directory.Exists(Path.Combine(source, "sub", "loop")));
    }

    [Fact]
    public async Task PermanentDelete_RemovesTree()
    {
        var source = Path.Combine(_root, "delete-me");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        await File.WriteAllTextAsync(Path.Combine(source, "sub", "a.txt"), "a");

        using var service = new FileOperationService(new TrashService());
        var item = await WaitAsync(service, Task.FromResult(service.Delete([Source(source)], true)));

        Assert.Equal(OperationState.Completed, item.State);
        Assert.False(Directory.Exists(source));
    }

    private static OperationSource Source(string path)
    {
        var info = Directory.Exists(path) ? new DirectoryInfo(path) : (FileSystemInfo)new FileInfo(path);
        var isDir = info is DirectoryInfo;
        return new OperationSource(LocalFileSystem.Instance, new FileEntry
        {
            Name = info.Name,
            Path = info.FullName,
            Kind = isDir ? EntryKind.Directory : EntryKind.File,
            Size = isDir ? 0 : ((FileInfo)info).Length,
            Modified = info.LastWriteTimeUtc,
        });
    }
}

public sealed class ArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoa-arch-" + Guid.NewGuid().ToString("N"));
    public ArchiveTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Theory]
    [InlineData("test.zip")]
    [InlineData("test.tar")]
    [InlineData("test.tar.gz")]
    public async Task CreateAndExtract_RoundTrip(string archiveName)
    {
        var folder = Path.Combine(_root, "input");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        await File.WriteAllTextAsync(Path.Combine(folder, "hello.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(folder, "sub", "deep.txt"), "deep");

        var archive = Path.Combine(_root, archiveName);
        await ArchiveService.CreateAsync([folder], archive);

        var output = Path.Combine(_root, "output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var extracted = await ArchiveService.ExtractAsync(archive, output);
        Assert.True(File.Exists(Path.Combine(extracted, "input", "hello.txt")));
        Assert.Equal("deep", await File.ReadAllTextAsync(Path.Combine(extracted, "input", "sub", "deep.txt")));
    }
}

public sealed class ChecksumTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoa-hash-" + Guid.NewGuid().ToString("N"));
    public ChecksumTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Sha256_MatchesKnownValue()
    {
        var path = Path.Combine(_root, "abc.txt");
        await File.WriteAllTextAsync(path, "abc");
        var hash = await ChecksumService.Sha256Async(LocalFileSystem.Instance, path);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }
}
