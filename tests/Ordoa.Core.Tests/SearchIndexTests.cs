using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class SearchIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ordoa-index-{Guid.NewGuid():N}");
    private readonly string _index;
    private readonly SearchIndexService _service;

    public SearchIndexTests()
    {
        _root = Path.Combine(_root, "files");
        _index = Path.Combine(Path.GetTempPath(), $"ordoa-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        File.WriteAllText(Path.Combine(_root, "alpha.txt"), "alpha");
        File.WriteAllText(Path.Combine(_root, "alphabet.log"), "alphabet");
        File.WriteAllText(Path.Combine(_root, "beta.txt"), "beta");
        File.WriteAllText(Path.Combine(_root, "nested", "gamma.txt"), "gamma");
        File.WriteAllText(Path.Combine(_root, ".hidden.txt"), "hidden");
        _service = new SearchIndexService(_index);
    }

    [Fact]
    public async Task BuildAndSearch_FindsNamesAndMetadata()
    {
        await _service.BuildAsync(Config());

        var alpha = await CollectAsync("alpha");
        Assert.Equal(2, alpha.Count);
        Assert.Contains(alpha, entry => entry.Name == "alpha.txt");
        Assert.Contains(alpha, entry => entry.Name == "alphabet.log");

        var textFiles = await CollectAsync("ext:txt");
        Assert.Contains(textFiles, entry => entry.Name == "alpha.txt");
        Assert.Contains(textFiles, entry => entry.Name == "beta.txt");
        Assert.DoesNotContain(textFiles, entry => entry.Name == "alphabet.log");
    }

    [Fact]
    public async Task Search_RespectsHiddenOption()
    {
        await _service.BuildAsync(Config());

        Assert.DoesNotContain(await CollectAsync("hidden"), entry => entry.Name == ".hidden.txt");
        Assert.Contains(await CollectAsync("hidden", includeHidden: true), entry => entry.Name == ".hidden.txt");
    }

    [Fact]
    public async Task Search_SkipsExcludedDirectories()
    {
        await _service.BuildAsync(Config(excludes: ["nested"]));

        Assert.Empty(await CollectAsync("gamma"));
        Assert.Single(await CollectAsync("alpha.txt"));
    }

    [Fact]
    public async Task Delta_AddsAndRemovesEntries()
    {
        await _service.BuildAsync(Config());

        var added = Path.Combine(_root, "delta.txt");
        File.WriteAllText(added, "delta");
        _service.ApplyChange(added);
        Assert.Contains(await CollectAsync("delta"), entry => entry.Name == "delta.txt");

        _service.ApplyDelete(added);
        Assert.DoesNotContain(await CollectAsync("delta"), entry => entry.Name == "delta.txt");
    }

    [Fact]
    public async Task Search_FallsBackForContentQueries()
    {
        await _service.BuildAsync(Config());
        Assert.Empty(await CollectAsync("content:alpha"));
    }

    private SearchIndexConfig Config(IReadOnlyList<string>? excludes = null)
        => new(SearchIndexMode.Whitelist, [_root], [.. excludes ?? []]);

    private async Task<List<FileEntry>> CollectAsync(string query, bool includeHidden = false)
    {
        var result = new List<FileEntry>();
        await foreach (var entry in _service.SearchAsync(query, includeHidden, 1000))
            result.Add(entry);
        return result;
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* ignore */ }
        try { Directory.Delete(_index, recursive: true); } catch { /* ignore */ }
    }
}
