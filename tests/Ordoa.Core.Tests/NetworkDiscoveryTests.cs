using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class NetworkDiscoveryTests
{
    [Fact]
    public async Task Discover_CompletesWithoutThrowingWhenNoServicesRespond()
    {
        var results = await new NetworkDiscoveryService()
            .DiscoverAsync(TimeSpan.FromMilliseconds(120));

        Assert.NotNull(results);
        Assert.All(results, service => Assert.False(string.IsNullOrWhiteSpace(service.Name)));
    }

    [Fact]
    public void DiscoveredService_RequiresProtocolAndPortToUse()
    {
        Assert.False(new DiscoveredService("mDNS", "x", "host", 0, null, "").CanUse);
        Assert.True(new DiscoveredService("mDNS", "x", "host", 22,
            Ordoa.Core.Models.RemoteProtocol.Sftp, "").CanUse);
    }
}
