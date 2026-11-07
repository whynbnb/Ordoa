using System.Diagnostics;
using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class GitServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ordoa-git-" + Guid.NewGuid().ToString("N"));

    public GitServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task StatusTracksUntrackedStagedModifiedAndDirectories()
    {
        if (!GitAvailable())
            return;

        Run("init", "-q");
        var file = Path.Combine(_root, "tracked.txt");
        await File.WriteAllTextAsync(file, "one");

        var status = await GitService.GetStatusAsync(_root);
        Assert.NotNull(status);
        Assert.False(status.IsClean);
        var untracked = Assert.Single(status.Files);
        Assert.True(untracked.IsUntracked);
        Assert.Equal("?", untracked.Code);
        Assert.Contains(Path.TrimEndingDirectorySeparator(_root), status.ChangedDirectories);

        Run("add", "tracked.txt");
        status = await GitService.GetStatusAsync(_root);
        Assert.NotNull(status);
        var staged = Assert.Single(status.Files);
        Assert.True(staged.IsStaged);
        Assert.Equal("A", staged.Code);

        Run("-c", "user.name=Ordoa Test", "-c", "user.email=ordoa@example.test",
            "commit", "-q", "-m", "initial");
        status = await GitService.GetStatusAsync(_root);
        Assert.NotNull(status);
        Assert.True(status.IsClean);
        Assert.Equal(0, status.ChangedCount);

        await File.WriteAllTextAsync(file, "two");
        var nested = Path.Combine(_root, "sub", "deep");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "new.txt"), "new");

        status = await GitService.GetStatusAsync(_root);
        Assert.NotNull(status);
        Assert.Equal(2, status.ChangedCount);
        Assert.Contains(status.Files, change => change.IsModified);
        Assert.Contains(status.Files, change => change.IsUntracked);
        Assert.Contains(Path.Combine(_root, "sub"), status.ChangedDirectories);
    }


    [Fact]
    public async Task StatusReportsTrackedIgnoredAndSubmodulePaths()
    {
        if (!GitAvailable())
            return;

        Run("init", "-q");
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), "ignored.txt\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "clean.txt"), "clean");
        Run("add", ".gitignore", "clean.txt");
        Run("-c", "user.name=Ordoa Test", "-c", "user.email=ordoa@example.test",
            "commit", "-q", "-m", "initial");
        await File.WriteAllTextAsync(Path.Combine(_root, "ignored.txt"), "ignore me");

        var sha = RunCapture("rev-parse", "HEAD");
        Run("update-index", "--add", "--cacheinfo", $"160000,{sha},module");

        var status = await GitService.GetStatusAsync(_root);
        Assert.NotNull(status);
        Assert.Contains(Path.Combine(_root, "clean.txt"), status.TrackedPaths);
        Assert.Contains(Path.Combine(_root, "ignored.txt"), status.IgnoredPaths);
        Assert.Contains(Path.Combine(_root, "module"), status.SubmodulePaths);
    }

    [Fact]
    public async Task NonRepositoryReturnsNull()
    {
        Assert.Null(await GitService.GetStatusAsync(_root));
        Assert.Null(GitService.FindRepository(_root));
    }

    private string RunCapture(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(10000);
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    private void Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        Assert.True(process.ExitCode == 0, error);
    }

    private static bool GitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return false;
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
