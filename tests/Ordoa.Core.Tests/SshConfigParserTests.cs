using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class SshConfigParserTests
{
    [Fact]
    public void Parse_ReadsHostAliasesAndOverrides()
    {
        var entries = SshConfigParser.Parse(
        [
            "User global",
            "Host dev",
            "  HostName dev.example.com",
            "  User alice",
            "  Port 2222",
            "  IdentityFile ~/.ssh/id_dev",
            "",
            "Host db backup",
            "  HostName db.example.com",
            "  User bob",
        ]);

        Assert.Equal(3, entries.Count);
        var dev = Assert.Single(entries, entry => entry.Alias == "dev");
        Assert.Equal("dev.example.com", dev.HostName);
        Assert.Equal("alice", dev.User);
        Assert.Equal(2222, dev.Port);
        Assert.EndsWith("/.ssh/id_dev", dev.IdentityFile);
        Assert.Equal("bob", Assert.Single(entries, entry => entry.Alias == "db").User);
        Assert.Equal("db.example.com", Assert.Single(entries, entry => entry.Alias == "backup").HostName);
    }

    [Fact]
    public void Parse_UsesGlobalDefaultsAndDefaultAlias()
    {
        var entries = SshConfigParser.Parse(
        [
            "Host staging",
            "  Port 2200",
        ]);

        var staging = Assert.Single(entries);
        Assert.Equal("staging", staging.HostName);
        Assert.Equal(Environment.UserName, staging.User);
        Assert.Equal(2200, staging.Port);
    }

    [Fact]
    public void Parse_SkipsWildcardsAndMatchBlocks()
    {
        var entries = SshConfigParser.Parse(
        [
            "Host *",
            "  User ignored",
            "Host real",
            "  HostName real.example.com",
            "Match host ignored",
            "  Port 1234",
            "Host after",
            "  HostName after.example.com",
        ]);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Alias == "real");
        Assert.Contains(entries, entry => entry.Alias == "after");
        Assert.DoesNotContain(entries, entry => entry.Alias == "*");
    }

    [Fact]
    public void Parse_HandlesEqualsSyntaxQuotesAndComments()
    {
        var entries = SshConfigParser.Parse(
        [
            "Host quoted # comment",
            "  HostName=\"quoted.example.com\"",
            "  User = \"user name\"",
            "  ProxyJump bastion",
        ]);

        var entry = Assert.Single(entries);
        Assert.Equal("quoted.example.com", entry.HostName);
        Assert.Equal("user name", entry.User);
        Assert.Equal("bastion", entry.ProxyJump);
    }

    [Fact]
    public void Load_MissingFileReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ordoa-missing-{Guid.NewGuid():N}", "config");
        Assert.Empty(SshConfigParser.Load(path));
    }
}
