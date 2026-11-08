using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

public sealed class ElevatedScriptBuilderTests
{
    [Fact]
    public void Quote_HandlesSingleQuotes()
    {
        Assert.Equal("'a'\\''b'", ElevatedScriptBuilder.Quote("a'b"));
    }

    [Fact]
    public void Copy_BuildsRecursiveCopyAndMoveScripts()
    {
        var copy = ElevatedScriptBuilder.Copy(["/tmp/a file"], "/tmp/dest", move: false);
        Assert.Contains("mkdir -p -- '/tmp/dest'", copy);
        Assert.Contains("cp -a -- '/tmp/a file' '/tmp/dest/a file'", copy);

        var move = ElevatedScriptBuilder.Copy(["/tmp/a"], "/tmp/dest", move: true);
        Assert.Contains("mv -- '/tmp/a' '/tmp/dest/a'", move);
    }

    [Fact]
    public void ChmodAndChown_QuotePathsAndModes()
    {
        var chmod = ElevatedScriptBuilder.Chmod("755", ["/tmp/a"]);
        Assert.Contains("chmod 755 -- '/tmp/a'", chmod);
        var chown = ElevatedScriptBuilder.Chown("alice", "staff", ["/tmp/a"]);
        Assert.Contains("chown -- 'alice:staff' '/tmp/a'", chown);
    }

    [Fact]
    public void WindowsScripts_UseLiteralPaths()
    {
        var delete = ElevatedScriptBuilder.WindowsDelete([@"C:\a b\file.txt"]);
        Assert.Contains(@"Remove-Item -LiteralPath 'C:\a b\file.txt'", delete);

        var copy = ElevatedScriptBuilder.WindowsCopy([@"C:\a b"], @"C:\dest", move: false);
        Assert.Contains(@"New-Item -ItemType Directory", copy);
        Assert.Contains("Copy-Item -LiteralPath 'C:\\a b' -Destination '" +
                        Path.Combine(@"C:\dest", "a b") + "' -Recurse", copy);
    }

    [Fact]
    public void AppleScriptString_EscapesQuotesAndBackslashes()
    {
        Assert.Equal("a\\\\b\\\"c", ElevatedScriptBuilder.AppleScriptString("a\\b\"c"));
    }
}
