using Ordoa.Core.FileSystems;
using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class FolderCompareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ordoa-compare-{Guid.NewGuid():N}");
    private readonly string _left;
    private readonly string _right;

    public FolderCompareTests()
    {
        _left = Path.Combine(_root, "left");
        _right = Path.Combine(_root, "right");
        Directory.CreateDirectory(_left);
        Directory.CreateDirectory(_right);
    }

    [Fact]
    public async Task Compare_ReportsOnlyAndDifferentEntries()
    {
        await File.WriteAllTextAsync(Path.Combine(_left, "both.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(_right, "both.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(_left, "left.txt"), "left");
        await File.WriteAllTextAsync(Path.Combine(_right, "right.txt"), "right");
        await File.WriteAllTextAsync(Path.Combine(_left, "size.txt"), "longer");
        await File.WriteAllTextAsync(Path.Combine(_right, "size.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_left, "nested"));
        await File.WriteAllTextAsync(Path.Combine(_left, "nested", "child.txt"), "child");

        var items = await new FolderCompareService().CompareAsync(
            LocalFileSystem.Instance, _left, LocalFileSystem.Instance, _right,
            new FolderCompareOptions());

        Assert.Equal(FolderCompareState.Same, State(items, "both.txt"));
        Assert.Equal(FolderCompareState.OnlyLeft, State(items, "left.txt"));
        Assert.Equal(FolderCompareState.OnlyRight, State(items, "right.txt"));
        Assert.Equal(FolderCompareState.Different, State(items, "size.txt"));
        Assert.Equal(FolderCompareState.OnlyLeft, State(items, "nested"));
        Assert.Equal(FolderCompareState.OnlyLeft, State(items, "nested/child.txt"));
    }

    [Fact]
    public async Task Compare_ContentsDetectsSameSizeChanges()
    {
        await File.WriteAllTextAsync(Path.Combine(_left, "same-size.txt"), "abcd");
        await File.WriteAllTextAsync(Path.Combine(_right, "same-size.txt"), "wxyz");

        var metadataOnly = await new FolderCompareService().CompareAsync(
            LocalFileSystem.Instance, _left, LocalFileSystem.Instance, _right,
            new FolderCompareOptions(CompareContents: false));
        Assert.NotEqual(FolderCompareState.Different, State(metadataOnly, "same-size.txt"));

        var contents = await new FolderCompareService().CompareAsync(
            LocalFileSystem.Instance, _left, LocalFileSystem.Instance, _right,
            new FolderCompareOptions(CompareContents: true));
        Assert.Equal(FolderCompareState.Different, State(contents, "same-size.txt"));
    }

    [Fact]
    public async Task Compare_ReportsNewerSide()
    {
        var leftPath = Path.Combine(_left, "newer.txt");
        var rightPath = Path.Combine(_right, "newer.txt");
        await File.WriteAllTextAsync(leftPath, "same");
        await File.WriteAllTextAsync(rightPath, "same");
        File.SetLastWriteTimeUtc(leftPath, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(rightPath, DateTime.UtcNow.AddHours(-2));

        var items = await new FolderCompareService().CompareAsync(
            LocalFileSystem.Instance, _left, LocalFileSystem.Instance, _right,
            new FolderCompareOptions());

        Assert.Equal(FolderCompareState.LeftNewer, State(items, "newer.txt"));
    }

    private static FolderCompareState State(IReadOnlyList<FolderCompareItem> items, string path)
        => items.Single(item => item.RelativePath == path).State;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
