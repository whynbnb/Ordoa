using Ordoa.Core.FileSystems;
using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class TagServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ordoa-tags-" + Guid.NewGuid().ToString("N"));

    public TagServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public void StoresColorAndTagsAndPersists()
    {
        var store = Path.Combine(_root, "tags.json");
        var file = Path.Combine(_root, "note.txt");
        File.WriteAllText(file, "data");
        var tagService = new TagService(store);

        tagService.SetColor(LocalFileSystem.Instance, file, FileColor.Blue);
        tagService.SetTags(LocalFileSystem.Instance, file, ["work", "urgent", "work"]);

        var info = tagService.Get(LocalFileSystem.Instance, file);
        Assert.NotNull(info);
        Assert.Equal(FileColor.Blue, info.Color);
        Assert.Equal(["work", "urgent"], info.Tags);

        var reloaded = new TagService(store);
        var persisted = reloaded.Get(LocalFileSystem.Instance, file);
        Assert.NotNull(persisted);
        Assert.Equal(FileColor.Blue, persisted.Color);
        Assert.Equal(["work", "urgent"], persisted.Tags);
    }

    [Fact]
    public void ClearingRemovesEmptyEntries()
    {
        var store = Path.Combine(_root, "tags.json");
        var file = Path.Combine(_root, "note.txt");
        File.WriteAllText(file, "data");
        var tagService = new TagService(store);

        tagService.SetColor(LocalFileSystem.Instance, file, FileColor.Red);
        tagService.SetColor(LocalFileSystem.Instance, file, FileColor.None);
        Assert.Null(tagService.Get(LocalFileSystem.Instance, file));

        tagService.SetTags(LocalFileSystem.Instance, file, ["a"]);
        tagService.SetTags(LocalFileSystem.Instance, file, []);
        Assert.Null(tagService.Get(LocalFileSystem.Instance, file));
    }
}
