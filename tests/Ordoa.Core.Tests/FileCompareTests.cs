using System.Text;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class FileCompareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ordoa-compare-" + Guid.NewGuid().ToString("N"));

    public FileCompareTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task IdenticalTextIsReported()
    {
        var left = await WriteAsync("left.txt", "alpha\nbeta\n");
        var right = await WriteAsync("right.txt", "alpha\nbeta\n");

        var result = await FileCompareService.CompareAsync(LocalFileSystem.Instance, left, right);

        Assert.Equal(FileCompareKind.Identical, result.Kind);
        Assert.True(result.Identical);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.All(result.Lines, line => Assert.Equal(CompareLineKind.Same, line.Kind));
    }

    [Fact]
    public async Task ChangedLinesAreCounted()
    {
        var left = await WriteAsync("left.txt", "alpha\nbeta\ngamma\n");
        var right = await WriteAsync("right.txt", "alpha\nBETA\ngamma\ndelta\n");

        var result = await FileCompareService.CompareAsync(LocalFileSystem.Instance, left, right);

        Assert.Equal(FileCompareKind.Text, result.Kind);
        Assert.False(result.Identical);
        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Contains(result.Lines, line => line is { Kind: CompareLineKind.Removed, LeftText: "beta" });
        Assert.Contains(result.Lines, line => line is { Kind: CompareLineKind.Added, RightText: "delta" });
    }

    [Fact]
    public async Task LargeTextIsComparedAsBinaryWithoutTruncation()
    {
        var leftPath = Path.Combine(_root, "large-left.txt");
        var rightPath = Path.Combine(_root, "large-right.txt");
        var leftData = new byte[9 * 1024 * 1024];
        var rightData = new byte[9 * 1024 * 1024];
        Array.Fill(leftData, (byte)'a');
        Array.Fill(rightData, (byte)'a');
        rightData[^1] = (byte)'b';
        await File.WriteAllBytesAsync(leftPath, leftData);
        await File.WriteAllBytesAsync(rightPath, rightData);
        var left = (await LocalFileSystem.Instance.GetEntryAsync(leftPath))!;
        var right = (await LocalFileSystem.Instance.GetEntryAsync(rightPath))!;

        var result = await FileCompareService.CompareAsync(LocalFileSystem.Instance, left, right);

        Assert.Equal(FileCompareKind.Binary, result.Kind);
        Assert.False(result.Identical);
        Assert.Equal(leftData.Length - 1, result.FirstDifferenceOffset);
    }

    [Fact]
    public async Task BinaryDifferenceReportsFirstOffset()
    {
        var left = await WriteBytesAsync("left.bin", [1, 2, 3, 4]);
        var right = await WriteBytesAsync("right.bin", [1, 2, 9, 4]);

        var result = await FileCompareService.CompareAsync(LocalFileSystem.Instance, left, right);

        Assert.Equal(FileCompareKind.Binary, result.Kind);
        Assert.False(result.Identical);
        Assert.Equal(2, result.FirstDifferenceOffset);
    }

    private async Task<Ordoa.Core.Models.FileEntry> WriteAsync(string name, string content)
        => await WriteBytesAsync(name, Encoding.UTF8.GetBytes(content));

    private async Task<Ordoa.Core.Models.FileEntry> WriteBytesAsync(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, content);
        return (await LocalFileSystem.Instance.GetEntryAsync(path))!;
    }
}
