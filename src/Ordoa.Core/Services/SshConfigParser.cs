using System.Globalization;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

/// <summary>A concrete host alias parsed from an OpenSSH client configuration.</summary>
public sealed record SshConfigEntry(
    string Alias,
    string HostName,
    string User,
    int Port,
    string? IdentityFile,
    string? ProxyJump)
{
    public string DisplayTarget => $"{User}@{HostName}:{Port}";

    public string IdentityDisplay => string.IsNullOrWhiteSpace(IdentityFile)
        ? string.Empty
        : Path.GetFileName(IdentityFile);
}

/// <summary>
/// Reads the subset of ~/.ssh/config that maps cleanly onto Ordoa's SFTP
/// connections. Wildcard-only and Match blocks are ignored so importing never
/// creates aliases that OpenSSH would resolve differently.
/// </summary>
public static class SshConfigParser
{
    public static string DefaultPath => Path.Combine(AppPaths.Home, ".ssh", "config");

    public static IReadOnlyList<SshConfigEntry> Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : [];
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 SSH 配置失败: {path}", ex);
            return [];
        }
    }

    public static IReadOnlyList<SshConfigEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<SshConfigEntry>();
        var aliases = new List<string>();
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inMatchBlock = false;

        void Flush()
        {
            if (aliases.Count == 0)
                return;

            foreach (var alias in aliases)
            {
                if (alias.Length == 0 || alias.IndexOfAny(['*', '?', '!']) >= 0)
                    continue;

                var host = Value(block, global, "HostName") ?? alias;
                host = host.Replace("%h", alias, StringComparison.Ordinal);
                var user = Value(block, global, "User") ?? Environment.UserName;
                var portText = Value(block, global, "Port");
                var port = int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var parsedPort) && parsedPort is > 0 and <= 65535
                    ? parsedPort
                    : 22;
                var identity = Value(block, global, "IdentityFile");
                identity = Expand(identity, user);
                var proxyJump = Value(block, global, "ProxyJump");

                if (entries.All(entry => !string.Equals(entry.Alias, alias, StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Add(new SshConfigEntry(alias, host, user, port, identity, proxyJump));
                }
            }
        }

        foreach (var raw in lines)
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;

            var (key, value) = SplitOption(line);
            if (key.Length == 0)
                continue;

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                aliases = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
                block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                inMatchBlock = false;
                continue;
            }

            if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                aliases = [];
                block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                inMatchBlock = true;
                continue;
            }

            if (inMatchBlock)
                continue;

            if (aliases.Count == 0)
            {
                if (!global.ContainsKey(key))
                    global[key] = value;
            }
            else if (!block.ContainsKey(key))
            {
                block[key] = value;
            }
        }

        Flush();
        return entries
            .OrderBy(entry => entry.Alias, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? Value(Dictionary<string, string> block,
        Dictionary<string, string> global, string key)
    {
        if (block.TryGetValue(key, out var value) && value.Length > 0)
            return value;
        return global.TryGetValue(key, out value) && value.Length > 0 ? value : null;
    }

    private static string? Expand(string? value, string user)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim().Trim('"');
        value = value.Replace("%d", AppPaths.Home, StringComparison.Ordinal)
            .Replace("%u", user, StringComparison.Ordinal)
            .Replace("%h", string.Empty, StringComparison.Ordinal);
        if (value == "~")
            value = AppPaths.Home;
        else if (value.StartsWith("~/", StringComparison.Ordinal))
            value = Path.Combine(AppPaths.Home, value[2..]);

        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return value;
        }
    }

    private static (string Key, string Value) SplitOption(string line)
    {
        var separator = line.IndexOf('=');
        var whitespace = line.IndexOfAny([' ', '\t']);
        if (separator > 0 && (whitespace < 0 || separator < whitespace))
        {
            return (line[..separator].Trim(), line[(separator + 1)..].Trim().Trim('"'));
        }

        if (whitespace < 0)
            return (line.Trim(), string.Empty);

        var rest = line[(whitespace + 1)..].Trim();
        if (rest.StartsWith('='))
            rest = rest[1..].Trim();
        return (line[..whitespace].Trim(), rest.Trim('"'));
    }

    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuotes = !inQuotes;
            else if (line[i] == '#' && !inQuotes && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                return line[..i];
        }
        return line;
    }
}
