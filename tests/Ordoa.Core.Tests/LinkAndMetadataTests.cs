using System.Diagnostics;
using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

public sealed class LinkAndMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ordoa-links-" + Guid.NewGuid().ToString("N"));

    public LinkAndMetadataTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public void CreatesAbsoluteAndRelativeSymbolicLinks()
    {
        var target = Path.Combine(_root, "target.txt");
        File.WriteAllText(target, "data");

        var absolute = Path.Combine(_root, "absolute.txt");
        LinkService.CreateSymbolicLink(target, absolute, relative: false);
        Assert.Equal(target, new FileInfo(absolute).LinkTarget);

        var relative = Path.Combine(_root, "relative.txt");
        LinkService.CreateSymbolicLink(target, relative, relative: true);
        Assert.Equal("target.txt", new FileInfo(relative).LinkTarget);
        Assert.Equal("data", File.ReadAllText(relative));
    }

    [Fact]
    public void CreatesHardLink()
    {
        var target = Path.Combine(_root, "hard-target.txt");
        File.WriteAllText(target, "hard");
        var linkPath = Path.Combine(_root, "hard-link.txt");

        LinkService.CreateHardLink(target, linkPath);

        Assert.True(File.Exists(linkPath));
        File.WriteAllText(linkPath, "changed");
        Assert.Equal("changed", File.ReadAllText(target));
    }

    [Fact]
    public void CopiesExtendedAttributes()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var source = Path.Combine(_root, "source.txt");
        var destination = Path.Combine(_root, "destination.txt");
        File.WriteAllText(source, "data");
        File.WriteAllText(destination, "data");

        if (!Run("setfattr", "-n", "user.ordoa", "-v", "hello", source))
            return;

        ExtendedMetadata.CopyAttributes(source, destination);

        var value = RunCapture("getfattr", "--only-values", "-n", "user.ordoa", "--", destination);
        Assert.Equal("hello", value);
    }

    private static bool Run(string file, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null)
                return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string RunCapture(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var value = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(5000);
        return value;
    }
}
