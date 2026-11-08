using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class SshProxyJumpTests
{
    [Fact]
    public void Parse_ReadsUserAndPort()
    {
        var hop = Assert.Single(SshProxyJump.Parse("alice@bastion.example.com:2222", "default"));
        Assert.Equal("alice", hop.User);
        Assert.Equal("bastion.example.com", hop.Host);
        Assert.Equal(2222, hop.Port);
    }

    [Fact]
    public void Parse_SupportsMultipleHopsAndIpv6()
    {
        var hops = SshProxyJump.Parse("bastion,ssh://bob@[::1]:2200", "default");
        Assert.Equal(2, hops.Count);
        Assert.Equal("default", hops[0].User);
        Assert.Equal("::1", hops[1].Host);
        Assert.Equal(2200, hops[1].Port);
        Assert.Equal("bob", hops[1].User);
    }

    [Fact]
    public void Parse_NoneMeansNoJump()
    {
        Assert.Empty(SshProxyJump.Parse("none", "default"));
        Assert.Empty(SshProxyJump.Parse(null, "default"));
    }
}
