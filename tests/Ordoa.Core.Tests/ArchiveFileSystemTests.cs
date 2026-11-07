using System.Diagnostics;
using System.Text;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

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

    [Fact]
    public async Task OpensAndReadsSevenZipEntries()
    {
        if (!SevenZipTool.IsAvailable)
            return;

        var source = Path.Combine(_root, "seven");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "hello 7z");
        await File.WriteAllTextAsync(Path.Combine(source, "sub", "b.txt"), "nested 7z");

        var archivePath = Path.Combine(_root, "sample.7z");
        RunSevenZip("a", "-t7z", archivePath, Path.Combine(source, "a.txt"), Path.Combine(source, "sub"));

        var archive = await ArchiveFileSystem.OpenAsync(archivePath);
        try
        {
            var root = await archive.ListAsync("/");
            Assert.Contains(root, entry => entry.Name == "a.txt");
            Assert.Contains(root, entry => entry.Name == "sub" && entry.IsDirectory);

            await using var stream = await archive.OpenReadAsync("/a.txt");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal("hello 7z", await reader.ReadToEndAsync());
        }
        finally
        {
            await archive.DisposeAsync();
        }
    }

    [Fact]
    public async Task EditsSevenZipArchiveInPlace()
    {
        if (!SevenZipTool.IsAvailable)
            return;

        var source = Path.Combine(_root, "edit");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(source, "b.txt"), "b");
        var archivePath = Path.Combine(_root, "edit.7z");
        RunSevenZip("a", "-t7z", archivePath, Path.Combine(source, "a.txt"),
            Path.Combine(source, "b.txt"));

        await ArchiveEditService.DeleteAsync(archivePath, ArchiveFormat.SevenZip, ["a.txt"]);
        await ArchiveEditService.RenameAsync(archivePath, ArchiveFormat.SevenZip, "b.txt", "c.txt");
        var addFile = Path.Combine(source, "d.txt");
        await File.WriteAllTextAsync(addFile, "d");
        await ArchiveEditService.AddAsync(archivePath, ArchiveFormat.SevenZip, "/sub",
        [
            (addFile, "d.txt"),
        ]);

        var archive = await ArchiveFileSystem.OpenAsync(archivePath);
        try
        {
            var root = await archive.ListAsync("/");
            Assert.DoesNotContain(root, entry => entry.Name == "a.txt");
            Assert.Contains(root, entry => entry.Name == "c.txt");
            var sub = await archive.ListAsync("/sub");
            Assert.Contains(sub, entry => entry.Name == "d.txt");
        }
        finally
        {
            await archive.DisposeAsync();
        }
    }

    [Fact]
    public async Task EditsZipArchiveInPlace()
    {
        var source = Path.Combine(_root, "zipedit");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(source, "b.txt"), "b");
        var archivePath = Path.Combine(_root, "edit.zip");
        await ArchiveService.CreateAsync([source], archivePath);

        await ArchiveEditService.DeleteAsync(archivePath, ArchiveFormat.Zip, ["zipedit/a.txt"]);
        await ArchiveEditService.RenameAsync(archivePath, ArchiveFormat.Zip, "zipedit/b.txt",
            "zipedit/c.txt");
        var addFile = Path.Combine(source, "d.txt");
        await File.WriteAllTextAsync(addFile, "d");
        await ArchiveEditService.AddAsync(archivePath, ArchiveFormat.Zip, "/zipedit",
        [
            (addFile, "d.txt"),
        ]);

        var archive = await ArchiveFileSystem.OpenAsync(archivePath);
        try
        {
            var entries = await archive.ListAsync("/zipedit");
            Assert.DoesNotContain(entries, entry => entry.Name == "a.txt");
            Assert.Contains(entries, entry => entry.Name == "c.txt");
            Assert.Contains(entries, entry => entry.Name == "d.txt");
        }
        finally
        {
            await archive.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreatesSevenZipArchive()
    {
        if (!SevenZipTool.IsAvailable)
            return;

        var source = Path.Combine(_root, "data.txt");
        await File.WriteAllTextAsync(source, "made with 7z");
        var archivePath = Path.Combine(_root, "created.7z");

        await ArchiveService.Create7zAsync([source], archivePath, 5);

        var archive = await ArchiveFileSystem.OpenAsync(archivePath);
        try
        {
            await using var stream = await archive.OpenReadAsync("/data.txt");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal("made with 7z", await reader.ReadToEndAsync());
        }
        finally
        {
            await archive.DisposeAsync();
        }
    }

    [Fact]
    public async Task EncryptedSevenZipRequiresPassword()
    {
        if (!SevenZipTool.IsAvailable)
            return;

        var source = Path.Combine(_root, "secret.txt");
        await File.WriteAllTextAsync(source, "top secret");
        var archivePath = Path.Combine(_root, "secret.7z");
        RunSevenZip("a", "-t7z", "-psecret", archivePath, source);

        await Assert.ThrowsAsync<ArchivePasswordRequiredException>(
            () => ArchiveFileSystem.OpenAsync(archivePath));

        var archive = await ArchiveFileSystem.OpenAsync(archivePath, default, "secret");
        try
        {
            await using var stream = await archive.OpenReadAsync("/secret.txt");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal("top secret", await reader.ReadToEndAsync());
        }
        finally
        {
            await archive.DisposeAsync();
        }
    }

    [Fact]
    public async Task SearchesInsideZipArchive()
    {
        var source = Path.Combine(_root, "searchable");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "readme.txt"), "hello search");
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "code.cs"), "class Search {}");

        var archivePath = Path.Combine(_root, "searchable.zip");
        await ArchiveService.CreateAsync([source], archivePath);

        var archive = await ArchiveFileSystem.OpenAsync(archivePath);
        try
        {
            var results = new List<Ordoa.Core.Models.FileEntry>();
            await foreach (var entry in new SearchService().SearchAsync(archive, "/",
                               new SearchOptions("content:search", IncludeHidden: true, MaxDepth: 4)))
                results.Add(entry);

            var names = results.Select(entry => entry.Name).ToList();
            Assert.Contains("readme.txt", names);
            Assert.Contains("code.cs", names);
        }
        finally
        {
            await archive.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExtractsOnlySelectedZipEntries()
    {
        var source = Path.Combine(_root, "selection");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        await File.WriteAllTextAsync(Path.Combine(source, "keep.txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(source, "skip.txt"), "skip");
        await File.WriteAllTextAsync(Path.Combine(source, "sub", "nested.txt"), "nested");

        var archivePath = Path.Combine(_root, "selection.zip");
        await ArchiveService.CreateAsync([source], archivePath);

        var destination = Path.Combine(_root, "extracted");
        await ArchiveService.ExtractSelectionAsync(archivePath, destination, ["selection/keep.txt"]);

        Assert.True(File.Exists(Path.Combine(destination, "selection", "keep.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "selection", "skip.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "selection", "sub", "nested.txt")));
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

    private static void RunSevenZip(params string[] arguments)
    {
        var start = new ProcessStartInfo(SevenZipTool.Executable!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(20000);
        Assert.True(process.ExitCode == 0, error);
    }
}
