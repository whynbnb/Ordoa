using System.Security.Cryptography;
using System.Text;

using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public enum KnownHostsStatus
{
    Unknown,
    Match,
    Mismatch,
    Revoked,
}

public sealed record KnownHostsResult(KnownHostsStatus Status, string? KeyType);

/// <summary>
/// Reads OpenSSH known_hosts files so SFTP connections can reject changed
/// server keys before authenticating. Supports plain, wildcard, port-qualified
/// and hashed host entries.
/// </summary>
public sealed class KnownHostsStore
{
    private readonly IReadOnlyList<string> _paths;

    public static KnownHostsStore Default { get; } = new(GetDefaultPaths());

    public KnownHostsStore(IEnumerable<string> paths)
    {
        _paths = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> Paths => _paths;

    public KnownHostsResult Check(string host, int port, string keyType, byte[] hostKey)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(keyType) || hostKey.Length == 0)
            return new KnownHostsResult(KnownHostsStatus.Unknown, null);

        var hostKnown = false;
        string? matchedKeyType = null;
        var revoked = false;

        foreach (var path in _paths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    if (!TryParseLine(line, out var marker, out var hosts, out var keyTypeFromFile, out var key))
                        continue;
                    if (!HostListMatches(hosts, host, port))
                        continue;

                    if (marker == "@revoked")
                    {
                        if (key.AsSpan().SequenceEqual(hostKey))
                            revoked = true;
                        continue;
                    }

                    if (marker is not null)
                        continue;

                    // known_hosts stores one entry per host key type. A missing
                    // type is treated like an unknown host, while a different
                    // key of the same type is a real mismatch.
                    if (!string.Equals(keyType, keyTypeFromFile, StringComparison.OrdinalIgnoreCase))
                        continue;

                    hostKnown = true;
                    if (key.AsSpan().SequenceEqual(hostKey))
                        matchedKeyType ??= keyTypeFromFile;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                Log.Warn($"无法读取 known_hosts 文件: {path}", ex);
            }
        }

        if (revoked)
            return new KnownHostsResult(KnownHostsStatus.Revoked, matchedKeyType);
        if (matchedKeyType is not null)
            return new KnownHostsResult(KnownHostsStatus.Match, matchedKeyType);
        if (hostKnown)
            return new KnownHostsResult(KnownHostsStatus.Mismatch, null);
        return new KnownHostsResult(KnownHostsStatus.Unknown, null);
    }

    private static bool TryParseLine(string line, out string? marker, out string hosts, out string keyType,
        out byte[] key)
    {
        marker = null;
        hosts = string.Empty;
        keyType = string.Empty;
        key = [];

        var comment = line.IndexOf('#');
        if (comment >= 0)
            line = line[..comment];
        line = line.Trim();
        if (line.Length == 0)
            return false;

        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        if (fields.Length > 0 && fields[0].StartsWith('@'))
        {
            marker = fields[0];
            index = 1;
        }

        if (fields.Length < index + 3)
            return false;

        hosts = fields[index];
        keyType = fields[index + 1];
        try
        {
            key = Convert.FromBase64String(fields[index + 2]);
        }
        catch (FormatException)
        {
            return false;
        }

        return key.Length > 0;
    }

    private static bool HostListMatches(string hostList, string host, int port)
    {
        var matched = false;
        foreach (var rawPattern in hostList.Split(','))
        {
            var pattern = rawPattern.Trim();
            if (pattern.Length == 0)
                continue;

            var negated = pattern[0] == '!';
            if (negated)
                pattern = pattern[1..];

            if (!HostPatternMatches(pattern, host, port))
                continue;
            if (negated)
                return false;

            matched = true;
        }

        return matched;
    }

    private static bool HostPatternMatches(string pattern, string host, int port)
    {
        if (pattern.StartsWith("|1|", StringComparison.Ordinal))
            return HashedHostMatches(pattern, HostToken(host, port));

        if (pattern.StartsWith('['))
        {
            var closing = pattern.IndexOf("]:", StringComparison.Ordinal);
            if (closing < 0)
                return false;
            if (!int.TryParse(pattern[(closing + 2)..], out var entryPort) || entryPort != port)
                return false;
            return WildcardMatch(pattern[1..closing], host);
        }

        // As in OpenSSH, an unqualified host pattern only applies to the
        // default SSH port; other ports use the [host]:port form.
        return port is 0 or 22 && WildcardMatch(pattern, host);
    }

    private static string HostToken(string host, int port)
        => port is 0 or 22 ? host : $"[{host}]:{port}";

    private static bool HashedHostMatches(string pattern, string hostToken)
    {
        var parts = pattern.Split('|');
        if (parts.Length != 4 || parts[1] != "1")
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = parts[3];
            using var hmac = new HMACSHA1(salt);
            var actual = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(hostToken)));
            return FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool WildcardMatch(string pattern, string value)
    {
        var p = 0;
        var v = 0;
        var star = -1;
        var retry = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                retry = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++retry;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    private static bool CharEquals(char left, char right)
        => char.ToLowerInvariant(left) == char.ToLowerInvariant(right);

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static IReadOnlyList<string> GetDefaultPaths()
    {
        var overridePath = Environment.GetEnvironmentVariable("ORDOA_KNOWN_HOSTS");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return [overridePath];

        var paths = new List<string>();
        var home = AppPaths.Home;
        if (!string.IsNullOrWhiteSpace(home))
        {
            paths.Add(Path.Combine(home, ".ssh", "known_hosts"));
            paths.Add(Path.Combine(home, ".ssh", "known_hosts2"));
        }

        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
                paths.Add(Path.Combine(programData, "ssh", "ssh_known_hosts"));
        }
        else
        {
            paths.Add("/etc/ssh/ssh_known_hosts");
            paths.Add("/etc/ssh/ssh_known_hosts2");
        }

        return paths;
    }
}
