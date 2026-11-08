using System.Text;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

public sealed class CompressedTarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ordoa-zstd-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(".tar.zst", CompressedTarTool.Kind.Zstd)]
    [InlineData(".tar.xz", CompressedTarTool.Kind.Xz)]
    public async Task CompressedTar_CreateBrowseAndExtract(string extension, CompressedTarTool.Kind kind)
    {
        if (!CompressedTarTool.IsAvailable(kind))
            return;

        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "hello.txt"), "hello compressed tar");

        var archive = Path.Combine(_root, "bundle" + extension);
        await ArchiveService.CreateAsync([source], archive);

        await using (var fileSystem = await ArchiveFileSystem.OpenAsync(archive))
        {
            var entries = await fileSystem.ListAsync("/source");
            Assert.Contains(entries, entry => entry.Name == "hello.txt" && !entry.IsDirectory);
            await using var stream = await fileSystem.OpenReadAsync("/source/hello.txt");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal("hello compressed tar", await reader.ReadToEndAsync());
        }

        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(output);
        await ArchiveService.ExtractAsync(archive, output, createSubfolder: false);
        Assert.Equal("hello compressed tar", await File.ReadAllTextAsync(Path.Combine(output, "source", "hello.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }
}
