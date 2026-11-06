using System.Security.Cryptography;
using System.Text;

using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class KnownHostsTests : IDisposable
{
    private static readonly byte[] HostKey = Encoding.UTF8.GetBytes("host-key-one");
    private static readonly byte[] OtherKey = Encoding.UTF8.GetBytes("host-key-two");

    private readonly string _file = Path.Combine(Path.GetTempPath(), "ordoa-known-hosts-" + Guid.NewGuid().ToString("N"));
    private readonly KnownHostsStore _store;

    public KnownHostsTests()
    {
        _store = new KnownHostsStore([_file]);
    }

    public void Dispose()
    {
        try { File.Delete(_file); } catch { /* ignore */ }
    }

    private void Write(params string[] lines) => File.WriteAllLines(_file, lines);

    private static string Key(byte[] bytes) => Convert.ToBase64String(bytes);

    [Fact]
    public void PlainHost_MatchingKey_ReturnsMatch()
    {
        Write($"example.com ssh-ed25519 {Key(HostKey)}");

        var result = _store.Check("example.com", 22, "ssh-ed25519", HostKey);

        Assert.Equal(KnownHostsStatus.Match, result.Status);
        Assert.Equal("ssh-ed25519", result.KeyType);
    }

    [Fact]
    public void PlainHost_ChangedKey_ReturnsMismatch()
    {
        Write($"example.com ssh-ed25519 {Key(OtherKey)}");

        var result = _store.Check("example.com", 22, "ssh-ed25519", HostKey);

        Assert.Equal(KnownHostsStatus.Mismatch, result.Status);
    }

    [Fact]
    public void DifferentKeyType_ReturnsUnknown()
    {
        Write($"example.com ssh-rsa {Key(OtherKey)}");

        Assert.Equal(KnownHostsStatus.Unknown,
            _store.Check("example.com", 22, "ssh-ed25519", HostKey).Status);
    }

    [Fact]
    public void UnknownHost_ReturnsUnknown()
    {
        Write($"example.com ssh-ed25519 {Key(HostKey)}");

        Assert.Equal(KnownHostsStatus.Unknown,
            _store.Check("other.example.com", 22, "ssh-ed25519", HostKey).Status);
    }

    [Fact]
    public void HashedHost_DefaultPort_Matches()
    {
        var salt = Encoding.UTF8.GetBytes("0123456789abcdef");
        using var hmac = new HMACSHA1(salt);
        var hash = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes("example.com")));
        Write($"|1|{Convert.ToBase64String(salt)}|{hash} ssh-ed25519 {Key(HostKey)}");

        Assert.Equal(KnownHostsStatus.Match, _store.Check("example.com", 22, "ssh-ed25519", HostKey).Status);
    }

    [Fact]
    public void HashedHost_NonDefaultPort_UsesBracketedToken()
    {
        var salt = Encoding.UTF8.GetBytes("0123456789abcdef");
        using var hmac = new HMACSHA1(salt);
        var hash = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes("[example.com]:2222")));
        Write($"|1|{Convert.ToBase64String(salt)}|{hash} ssh-ed25519 {Key(HostKey)}");

        Assert.Equal(KnownHostsStatus.Match, _store.Check("example.com", 2222, "ssh-ed25519", HostKey).Status);
        Assert.Equal(KnownHostsStatus.Unknown, _store.Check("example.com", 22, "ssh-ed25519", HostKey).Status);
    }

    [Fact]
    public void PortQualifiedEntry_OnlyMatchesThatPort()
    {
        Write($"[example.com]:2222 ssh-ed25519 {Key(HostKey)}");

        Assert.Equal(KnownHostsStatus.Match, _store.Check("example.com", 2222, "ssh-ed25519", HostKey).Status);
        Assert.Equal(KnownHostsStatus.Unknown, _store.Check("example.com", 22, "ssh-ed25519", HostKey).Status);
    }

    [Fact]
    public void WildcardHost_MatchesSubdomain()
    {
        Write($"*.example.com ssh-ed25519 {Key(HostKey)}");

        Assert.Equal(KnownHostsStatus.Match, _store.Check("a.example.com", 22, "ssh-ed25519", HostKey).Status);
        Assert.Equal(KnownHostsStatus.Unknown, _store.Check("example.com", 22, "ssh-ed25519", HostKey).Status);
    }

    [Fact]
    public void RevokedEntry_OverridesMatchingEntry()
    {
        Write(
            $"example.com ssh-ed25519 {Key(HostKey)}",
            $"@revoked example.com ssh-ed25519 {Key(HostKey)}");

        Assert.Equal(KnownHostsStatus.Revoked, _store.Check("example.com", 22, "ssh-ed25519", HostKey).Status);
    }

    [Fact]
    public void CommentsAndMultipleKeys_AreHandled()
    {
        Write(
            "# comment",
            $"example.com ssh-rsa {Key(OtherKey)}",
            $"example.com ssh-ed25519 {Key(HostKey)}");

        var result = _store.Check("example.com", 22, "ssh-ed25519", HostKey);

        Assert.Equal(KnownHostsStatus.Match, result.Status);
        Assert.Equal("ssh-ed25519", result.KeyType);
    }
}
