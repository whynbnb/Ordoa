using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

[CollectionDefinition("Trash", DisableParallelization = true)]
public sealed class TrashCollection
{
}

[Collection("Trash")]
public sealed class TrashTests : IDisposable
{
    private readonly string _root;
    private readonly string? _oldDataHome;

    public TrashTests()
    {
        _root = Path.Combine(AppPaths.Home, "ordoa-trash-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _oldDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _oldDataHome);
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task MoveListRestoreAndEmpty()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var service = new TrashService();
        var source = Path.Combine(_root, "to-delete.txt");
        await File.WriteAllTextAsync(source, "trash me");

        var entry = await service.MoveToTrashAsync(source);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(entry.TrashPath));
        Assert.Equal(source, entry.OriginalPath);
        Assert.True(File.Exists(Path.Combine(AppPaths.TrashDirectory, "info", Path.GetFileName(entry.TrashPath) + ".trashinfo")));

        var listed = await service.GetEntriesAsync();
        Assert.Contains(listed, e => e.TrashPath == entry.TrashPath && e.OriginalPath == source);

        var restored = await service.RestoreAsync(entry);
        Assert.Equal(source, restored);
        Assert.Equal("trash me", await File.ReadAllTextAsync(source));

        var again = await service.MoveToTrashAsync(source);
        await service.DeletePermanentlyAsync(again.TrashPath);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(again.TrashPath));

        var folder = Path.Combine(_root, "folder");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "a");
        var folderEntry = await service.MoveToTrashAsync(folder);
        await service.DeletePermanentlyAsync(folderEntry.TrashPath);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task RestoreAutoRenamesWhenOriginalExists()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var service = new TrashService();
        var source = Path.Combine(_root, "same.txt");
        await File.WriteAllTextAsync(source, "original");
        var entry = await service.MoveToTrashAsync(source);
        await File.WriteAllTextAsync(source, "new file");

        var restored = await service.RestoreAsync(entry);
        Assert.NotEqual(source, restored);
        Assert.Equal("original", await File.ReadAllTextAsync(restored));
        Assert.Equal("new file", await File.ReadAllTextAsync(source));
    }
}
