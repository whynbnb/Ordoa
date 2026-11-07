using Ordoa.Core.FileSystems;
using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class SearchQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseExtractsPlainTermsAndExclusions()
    {
        var query = SearchQuery.Parse("report -node_modules -*.tmp", Now);

        Assert.Equal(["report"], query.Terms);
        Assert.Equal(["node_modules", "*.tmp"], query.Excludes);
        Assert.True(query.ShouldPruneDirectory("node_modules"));
        Assert.False(query.ShouldPruneDirectory("src"));
    }

    [Fact]
    public void ParseExtractsTypeExtensionAndSizeFilters()
    {
        var query = SearchQuery.Parse("*.cs ext:cs,json type:file size:>10MB", Now);

        Assert.Equal(["*.cs"], query.Terms);
        Assert.Equal(["cs", "json"], query.Extensions);
        Assert.Equal(SearchEntryType.File, query.Type);
        Assert.Equal(10L * 1024 * 1024 + 1, query.MinSize);
        Assert.Null(query.MaxSize);
    }

    [Fact]
    public void ParseSupportsSizeRangesAndModifiedAges()
    {
        var query = SearchQuery.Parse("size:1KB..2MB modified:>7d", Now);

        Assert.Equal(1024, query.MinSize);
        Assert.Equal(2L * 1024 * 1024, query.MaxSize);
        Assert.Equal(Now.AddDays(-7), query.ModifiedAfter);
    }

    [Fact]
    public void ParseSupportsQuotedContentTerms()
    {
        var query = SearchQuery.Parse("content:\"hello world\" ext:txt", Now);

        Assert.True(query.HasContent);
        Assert.Equal(["hello world"], query.ContentTerms);
        Assert.Equal(["txt"], query.Extensions);
    }

    [Fact]
    public void ParseExtractsTagAndColorFilters()
    {
        var query = SearchQuery.Parse("tag:work,urgent color:red ext:txt", Now);

        Assert.Equal(["work", "urgent"], query.Tags);
        Assert.Equal(Ordoa.Core.Models.FileColor.Red, query.Color);
        Assert.Equal(["txt"], query.Extensions);
        Assert.False(query.IsEmpty);
    }

    [Fact]
    public void ParseIgnoresUnknownColonTokens()
    {
        var query = SearchQuery.Parse("http://example.com", Now);

        Assert.Equal(["http://example.com"], query.Terms);
    }

    [Theory]
    [InlineData("10MB", 10L * 1024 * 1024)]
    [InlineData("1.5KB", 1536)]
    [InlineData("2 GB", 2L * 1024 * 1024 * 1024)]
    [InlineData("512", 512)]
    public void ParsesSizes(string text, long expected)
    {
        Assert.True(SearchQuery.TryParseSize(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void MatchesMetadataRequiresAllTerms()
    {
        var query = SearchQuery.Parse("report *.pdf", Now);
        var entry = Entry("quarterly-report.pdf", 2048);

        Assert.True(query.MatchesMetadata(entry));
        Assert.False(query.MatchesMetadata(Entry("report.txt", 2048)));
        Assert.False(query.MatchesMetadata(Entry("other.pdf", 2048)));
    }

    [Fact]
    public void MatchesMetadataAppliesTypeSizeAndTypeFilters()
    {
        var imageQuery = SearchQuery.Parse("type:image size:<1MB", Now);
        Assert.True(imageQuery.MatchesMetadata(Entry("photo.png", 512)));
        Assert.False(imageQuery.MatchesMetadata(Entry("photo.png", 2 * 1024 * 1024)));
        Assert.False(imageQuery.MatchesMetadata(Entry("notes.txt", 512)));
        Assert.False(imageQuery.MatchesMetadata(Entry("folder.png", 512, isDirectory: true)));
    }

    [Fact]
    public void MatchesMetadataUsesModifiedDates()
    {
        var query = SearchQuery.Parse("modified:>7d", Now);

        Assert.True(query.MatchesMetadata(Entry("new.txt", 1, modified: Now.AddDays(-2))));
        Assert.False(query.MatchesMetadata(Entry("old.txt", 1, modified: Now.AddDays(-10))));
        Assert.False(query.MatchesMetadata(Entry("unknown.txt", 1)));
    }

    private static FileEntry Entry(string name, long size, bool isDirectory = false,
        DateTimeOffset? modified = null) => new()
    {
        Name = name,
        Path = Path.Combine("/tmp", name),
        Kind = isDirectory ? EntryKind.Directory : EntryKind.File,
        Size = size,
        Modified = modified,
    };
}

public sealed class SearchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ordoa-search-" + Guid.NewGuid().ToString("N"));

    public SearchServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task FindsFilesByGlobAndPrunesExcludedFolders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "Program.cs"), "class Program {}");
        await File.WriteAllTextAsync(Path.Combine(_root, "node_modules", "ignored.cs"), "class Ignored {}");

        var results = await CollectAsync("*.cs -node_modules");

        Assert.Single(results);
        Assert.Equal("Program.cs", results[0].Name);
    }

    [Fact]
    public async Task SearchesFileContents()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "alpha TODO beta");
        await File.WriteAllTextAsync(Path.Combine(_root, "other.txt"), "alpha beta");
        await File.WriteAllTextAsync(Path.Combine(_root, "binary.bin"), "TODO\0binary");

        var results = await CollectAsync("content:TODO");

        Assert.Single(results);
        Assert.Equal("notes.txt", results[0].Name);
    }

    [Fact]
    public async Task PredicateFiltersResults()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "b");

        var service = new SearchService();
        var results = new List<FileEntry>();
        await foreach (var entry in service.SearchAsync(LocalFileSystem.Instance, _root,
                           new SearchOptions("*.txt", IncludeHidden: true, MaxDepth: 2,
                               Predicate: entry => entry.Name == "b.txt")))
            results.Add(entry);

        Assert.Single(results);
        Assert.Equal("b.txt", results[0].Name);
    }

    [Fact]
    public async Task AppliesSizeFilters()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "small.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_root, "large.txt"), new string('a', 2048));

        var results = await CollectAsync("size:>1KB");

        Assert.Single(results);
        Assert.Equal("large.txt", results[0].Name);
    }

    private async Task<List<FileEntry>> CollectAsync(string query)
    {
        var service = new SearchService();
        var results = new List<FileEntry>();
        await foreach (var entry in service.SearchAsync(LocalFileSystem.Instance, _root,
                           new SearchOptions(query, IncludeHidden: true, MaxDepth: 4)))
            results.Add(entry);
        return results;
    }
}
