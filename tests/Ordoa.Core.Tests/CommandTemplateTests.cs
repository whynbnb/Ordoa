using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class CommandTemplateTests
{
    [Fact]
    public void Expand_ReplacesFileAndDirectoryPlaceholders()
    {
        var result = CommandTemplateService.Expand(
            "echo %f %n %d %p %% %z",
            "/home/user",
            ["/home/user/a b.txt", "/home/user/c.txt"],
            quote: false);

        Assert.Equal("echo /home/user/a b.txt a b.txt /home/user /home/user % %z", result);
    }

    [Fact]
    public void Expand_QuotesEachSelectedPath()
    {
        var result = CommandTemplateService.Expand(
            "tool %f %F",
            "/home/user",
            ["/home/user/a b.txt", "/home/user/c'd.txt"],
            quote: true);

        Assert.Equal("tool '/home/user/a b.txt' '/home/user/a b.txt' '/home/user/c'\"'\"'d.txt'", result);
    }

    [Fact]
    public void Expand_WithoutSelectionLeavesValuesEmpty()
    {
        var result = CommandTemplateService.Expand("tool %f %d", "/tmp", [], quote: true);
        Assert.Equal("tool  '/tmp'", result);
    }

    [Fact]
    public void SplitArguments_KeepsDirectoryAndCommandAsSingleTokens()
    {
        var arguments = CommandTemplateService.SplitArguments(
            "--working-directory=%d -e sh -lc %c",
            "/tmp/a b",
            "printf '%s\\n' 'hello world'");

        Assert.Equal(
            ["--working-directory=/tmp/a b", "-e", "sh", "-lc", "printf '%s\\n' 'hello world'"],
            arguments);
    }

    [Fact]
    public void SplitArguments_HonoursQuotesInTemplate()
    {
        var arguments = CommandTemplateService.SplitArguments(
            "--title \"My Terminal\" --working-directory=%d",
            "/tmp/a b",
            null);

        Assert.Equal(["--title", "My Terminal", "--working-directory=/tmp/a b"], arguments);
    }

    [Theory]
    [InlineData("code %f", true)]
    [InlineData("git status", false)]
    [InlineData("echo %%f", false)]
    [InlineData("tool %p", true)]
    public void NeedsSelection_DetectsSelectionPlaceholders(string template, bool expected)
        => Assert.Equal(expected, CommandTemplateService.NeedsSelection(template));

    [Fact]
    public void QuotePosix_HandlesApostrophes()
        => Assert.Equal("'a'\"'\"'b'", CommandTemplateService.QuotePosix("a'b"));
}
