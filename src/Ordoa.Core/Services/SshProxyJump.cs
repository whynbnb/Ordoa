using System.Globalization;

namespace Ordoa.Core.Services;

public sealed record SshJumpHop(string User, string Host, int Port);

/// <summary>Parses the single- or multi-hop OpenSSH ProxyJump syntax.</summary>
public static class SshProxyJump
{
    public static IReadOnlyList<SshJumpHop> Parse(string? value, string defaultUser, int defaultPort = 22)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            return [];

        var hops = new List<SshJumpHop>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = raw;
            if (text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                text = text[6..];

            var user = defaultUser;
            var at = text.LastIndexOf('@');
            if (at > 0)
            {
                user = text[..at];
                text = text[(at + 1)..];
            }

            var host = text;
            var port = defaultPort;
            if (text.StartsWith('['))
            {
                var end = text.IndexOf(']');
                if (end > 0)
                {
                    host = text[1..end];
                    if (end + 1 < text.Length && text[end + 1] == ':' &&
                        int.TryParse(text[(end + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var bracketPort))
                    {
                        port = bracketPort;
                    }
                }
            }
            else
            {
                var colon = text.LastIndexOf(':');
                if (colon > 0 && text.IndexOf(':') == colon &&
                    int.TryParse(text[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var parsedPort))
                {
                    host = text[..colon];
                    port = parsedPort;
                }
            }

            if (host.Length == 0 || port is < 1 or > 65535)
                continue;
            hops.Add(new SshJumpHop(user, host, port));
        }

        return hops;
    }
}
