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
    public void Load_ExpandsIncludeGlobs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ordoa-ssh-{Guid.NewGuid():N}");
        try
        {
            var includeDir = Path.Combine(root, "config.d");
            Directory.CreateDirectory(includeDir);
            var config = Path.Combine(root, "config");
            File.WriteAllText(config, "Include config.d/*.conf\n");
            File.WriteAllText(Path.Combine(includeDir, "work.conf"),
                "Host work\n  HostName work.example.com\n  User alice\n");

            var entries = SshConfigParser.Load(config);
            var entry = Assert.Single(entries);
            Assert.Equal("work", entry.Alias);
            Assert.Equal("work.example.com", entry.HostName);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Parse_AppliesMatchingMatchBlocksOnly()
    {
        var entries = SshConfigParser.Parse(
        [
            "Host prod",
            "  User alice",
            "Match host prod",
            "  Port 2222",
            "  ProxyJump bastion",
            "Host other",
            "  User bob",
            "Match host staging",
            "  Port 2200",
        ]);

        var prod = Assert.Single(entries, entry => entry.Alias == "prod");
        Assert.Equal(2222, prod.Port);
        Assert.Equal("bastion", prod.ProxyJump);
        var other = Assert.Single(entries, entry => entry.Alias == "other");
        Assert.Equal(22, other.Port);
    }

    [Fact]
    public void Parse_DoesNotTreatHostPatternAsSubstring()
    {
        var entries = SshConfigParser.Parse(
        [
            "Host production",
            "  User alice",
            "Match host prod",
            "  Port 2222",
        ]);
        Assert.Equal(22, Assert.Single(entries).Port);
    }

    [Fact]
    public void Parse_AppliesUserMatchConditions()
    {
        var entries = SshConfigParser.Parse(
        [
            "Host prod",
            "  User alice",
            "Match user alice",
            "  Port 2022",
        ]);

        Assert.Equal(2022, Assert.Single(entries).Port);
    }

    [Fact]
    public void Load_MissingFileReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ordoa-missing-{Guid.NewGuid():N}", "config");
        Assert.Empty(SshConfigParser.Load(path));
    }
}
