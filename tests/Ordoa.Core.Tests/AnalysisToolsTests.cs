using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class AnalysisToolsTests
{
    [Fact]
    public void Treemap_SingleEntryFillsBounds()
    {
        var tiles = TreemapLayout.Create([Entry("a", 10)], 200, 100);
        var tile = Assert.Single(tiles);
        Assert.Equal(0, tile.Rect.X, 6);
        Assert.Equal(0, tile.Rect.Y, 6);
        Assert.Equal(200, tile.Rect.Width, 6);
        Assert.Equal(100, tile.Rect.Height, 6);
    }

    [Fact]
    public void Treemap_KeepsAreaProportionalAndInsideBounds()
    {
        var entries = new[]
        {
            Entry("a", 6), Entry("b", 5), Entry("c", 4),
            Entry("d", 3), Entry("e", 2), Entry("f", 1),
        };
        const double width = 320;
        const double height = 180;
        var tiles = TreemapLayout.Create(entries, width, height);

        Assert.Equal(entries.Length, tiles.Count);
        var total = entries.Sum(entry => entry.Value);
        foreach (var tile in tiles)
        {
            Assert.InRange(tile.Rect.X, 0, width);
            Assert.InRange(tile.Rect.Y, 0, height);
            Assert.InRange(tile.Rect.X + tile.Rect.Width, 0, width + 1e-6);
            Assert.InRange(tile.Rect.Y + tile.Rect.Height, 0, height + 1e-6);
            var expected = tile.Entry.Value / total * width * height;
            var actual = tile.Rect.Width * tile.Rect.Height;
            Assert.Equal(expected, actual, 6);
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                Assert.True(OverlapArea(tiles[i].Rect, tiles[j].Rect) < 1e-6,
                    $"{tiles[i].Entry.Label} overlaps {tiles[j].Entry.Label}");
            }
        }
    }

    [Fact]
    public void Treemap_EmptyInputReturnsNothing()
    {
        Assert.Empty(TreemapLayout.Create([], 100, 100));
        Assert.Empty(TreemapLayout.Create([Entry("zero", 0)], 100, 100));
        Assert.Empty(TreemapLayout.Create([Entry("a", 1)], 0, 100));
    }

    [Fact]
    public async Task DiskUsage_ComputesRecursiveSizesAndLargestFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteFile(root, "a.bin", 1024);
            WriteFile(root, "b.bin", 2048);
            WriteFile(root, "sub/c.bin", 4096);
            WriteFile(root, "sub/deep/d.bin", 512);

            var result = await DiskUsageScanner.ScanAsync(root);

            Assert.Equal(7680, result.Root.TotalSize);
            Assert.Equal(3072, result.Root.DirectFileSize);
            Assert.Equal(4, result.FileCount);
            Assert.Equal(3, result.DirectoryCount);
            Assert.Equal([4096L, 2048L, 1024L, 512L],
                result.LargestFiles.Select(file => file.Size).ToArray());

            var sub = Assert.Single(result.Root.Children);
            Assert.Equal("sub", sub.Name);
            Assert.Equal(4608, sub.TotalSize);
            var deep = Assert.Single(sub.Children);
            Assert.Equal(512, deep.TotalSize);

            var direct = DiskUsageScanner.GetDirectFiles(root, 10);
            Assert.Equal(2, direct.Count);
            Assert.Equal(3072, direct.Sum(file => file.Size));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DuplicateScanner_GroupsSameContentOnly()
    {
        var root = CreateTempDirectory();
        try
        {
            var content = new byte[100];
            Array.Fill(content, (byte)'A');
            var other = new byte[100];
            Array.Fill(other, (byte)'B');

            WriteFile(root, "one.txt", content);
            WriteFile(root, "sub/two.txt", content);
            WriteFile(root, "three.txt", other);
            WriteFile(root, "unique.txt", [1, 2, 3]);
            WriteFile(root, "empty-a", []);
            WriteFile(root, "empty-b", []);

            var result = await DuplicateScanner.ScanAsync(root);

            var group = Assert.Single(result.Groups);
            Assert.Equal(100, group.Size);
            Assert.Equal(2, group.Count);
            Assert.Equal(100, group.WastedBytes);
            Assert.Contains(group.Paths, path => path.EndsWith("one.txt", StringComparison.Ordinal));
            Assert.Contains(group.Paths, path => path.EndsWith("two.txt", StringComparison.Ordinal));
            Assert.Equal(3, result.CandidateFiles);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DuplicateScanner_QuickHashSurvivorsAreConfirmedWithFullHash()
    {
        var root = CreateTempDirectory();
        try
        {
            const int size = 256 * 1024;
            var first = new byte[size];
            Array.Fill(first, (byte)1);
            var differingMiddle = first.ToArray();
            differingMiddle[size / 2] = 2;

            WriteFile(root, "first.bin", first);
            WriteFile(root, "second.bin", first);
            WriteFile(root, "middle.bin", differingMiddle);

            var result = await DuplicateScanner.ScanAsync(root);

            var group = Assert.Single(result.Groups);
            Assert.Equal(2, group.Count);
            Assert.DoesNotContain(group.Paths,
                path => path.EndsWith("middle.bin", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DuplicateScanner_RespectsMinimumSize()
    {
        var root = CreateTempDirectory();
        try
        {
            WriteFile(root, "a.txt", [1, 2, 3]);
            WriteFile(root, "b.txt", [1, 2, 3]);

            var result = await DuplicateScanner.ScanAsync(root, minimumSize: 1024);
            Assert.Empty(result.Groups);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static TreemapEntry Entry(string label, double value)
        => new(label, label, value, "/tmp/" + label, true);

    private static double OverlapArea(TreemapRect a, TreemapRect b)
    {
        var width = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var height = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ordoa-analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string root, string relative, params byte[] bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteFile(string root, string relative, int size)
        => WriteFile(root, relative, new byte[size]);
}
