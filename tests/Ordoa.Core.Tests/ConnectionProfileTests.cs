using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

public sealed class ConnectionProfileTests
{
    [Fact]
    public void SshJumps_RoundTripAndCloneIndependently()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ordoa-connections-{Guid.NewGuid():N}.json");
        try
        {
            var profile = new ConnectionProfile
            {
                Name = "target",
                Protocol = RemoteProtocol.Sftp,
                Host = "target.example.com",
                Username = "alice",
                SshJumps =
                [
                    new SshJumpProfile
                    {
                        Id = "jump-1",
                        Host = "bastion",
                        Port = 2222,
                        Username = "ops",
                        SshAuth = SshAuthMethod.PrivateKey,
                        PrivateKeyPath = "/home/alice/.ssh/jump",
                    },
                ],
            };
            JsonFile.Save(path, new List<ConnectionProfile> { profile });
            var loaded = JsonFile.Load<List<ConnectionProfile>>(path);
            var jump = Assert.Single(Assert.Single(loaded!).SshJumps);
            Assert.Equal("jump-1", jump.Id);
            Assert.Equal("bastion", jump.Host);
            Assert.Equal(2222, jump.Port);
            Assert.Equal(SshAuthMethod.PrivateKey, jump.SshAuth);

            var clone = profile.Clone();
            clone.SshJumps[0].Host = "changed";
            Assert.Equal("bastion", profile.SshJumps[0].Host);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
