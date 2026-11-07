using System.Text;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class ArchiveFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ordoa-archive-" + Guid.NewGuid().ToString("N"));

    public ArchiveFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Theory]
    [InlineData(".zip")]
    [InlineData(".tar")]
    [InlineData(".tar.gz")]
    public async Task OpensAndReadsEntries(string extension)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "hello archive");
        await File.WriteAllTextAsync(Path.Combine(source, "sub", "b.txt"), "nested file");

        var archivePath = Path.Combine(_root, "sample" + extension);
        await ArchiveService.CreateAsync([source], archivePath);

        var archive = await ArchiveFileSystem.OpenAsync(archivePath);
        try
        {
            var root = await archive.ListAsync("/");
            var sourceDirectory = Assert.Single(root);
            Assert.Equal("source", sourceDirectory.Name);
            Assert.True(sourceDirectory.IsDirectory);

            var entries = await archive.ListAsync("/source");
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry.Name == "a.txt" && entry.Size == 13);

            await using (var stream = await archive.OpenReadAsync("/source/a.txt"))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                Assert.Equal("hello archive", await reader.ReadToEndAsync());
            }

            var nested = await archive.ListAsync("/source/sub");
            var file = Assert.Single(nested);
            Assert.Equal("b.txt", file.Name);
        }
        finally
        {
            await archive.DisposeAsync();
        }
    }
}
