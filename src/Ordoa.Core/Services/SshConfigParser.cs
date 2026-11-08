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
/// connections. Includes are expanded recursively, and simple Match blocks
/// (all/host/originalhost/user) are applied to concrete Host aliases.
/// </summary>
public static class SshConfigParser
{
    public static string DefaultPath => Path.Combine(AppPaths.Home, ".ssh", "config");

    public static IReadOnlyList<SshConfigEntry> Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
                return [];
            var lines = new List<string>();
            ReadWithIncludes(Path.GetFullPath(path), lines, new HashSet<string>(StringComparer.Ordinal), 0);
            return Parse(lines);
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
        var active = true;

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
                var identity = Expand(Value(block, global, "IdentityFile"), user);
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
                active = true;
                continue;
            }

            if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                active = aliases.Count > 0 && MatchesConditions(value, aliases, block, global);
                continue;
            }

            if (!active)
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

    private static bool MatchesConditions(string value, IReadOnlyList<string> aliases,
        Dictionary<string, string> block, Dictionary<string, string> global)
    {
        var tokens = Tokenize(value);
        var index = 0;
        var matched = false;
        while (index < tokens.Count)
        {
            var condition = tokens[index++].ToLowerInvariant();
            if (condition == "all")
            {
                matched = true;
                continue;
            }
            if (index >= tokens.Count)
                return false;
            var patterns = tokens[index++];
            switch (condition)
            {
                case "host":
                case "originalhost":
                    if (!aliases.Any(alias => MatchPattern(alias, patterns)))
                        return false;
                    matched = true;
                    break;
                case "user":
                    var user = Value(block, global, "User") ?? Environment.UserName;
                    if (!MatchPattern(user, patterns))
                        return false;
                    matched = true;
                    break;
                case "final":
                    matched = true;
                    break;
                case "exec":
                case "localnetwork":
                case "tagged":
                case "canonical":
                    return false;
                default:
                    return false;
            }
        }
        return matched;
    }

    private static bool MatchPattern(string value, string patterns)
    {
        var matched = false;
        foreach (var raw in patterns.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith('!'))
            {
                if (GlobMatcher.IsMatch(value, raw[1..]))
                    return false;
                continue;
            }
            if (GlobMatcher.IsMatch(value, raw))
                matched = true;
        }
        return matched;
    }

    private static List<string> Tokenize(string value)
    {
        var result = new List<string>();
        var builder = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var character in value)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (builder.Length > 0)
                {
                    result.Add(builder.ToString());
                    builder.Clear();
                }
                continue;
            }
            builder.Append(character);
        }
        if (builder.Length > 0)
            result.Add(builder.ToString());
        return result;
    }

    private static void ReadWithIncludes(string path, List<string> output, HashSet<string> stack, int depth)
    {
        if (depth > 12 || !stack.Add(path))
            return;
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = StripComment(raw).Trim();
                if (line.Length == 0)
                    continue;
                var (key, value) = SplitOption(line);
                if (!key.Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add(raw);
                    continue;
                }

                foreach (var include in ResolveIncludes(value, Path.GetDirectoryName(path)!))
                    ReadWithIncludes(include, output, stack, depth + 1);
            }
        }
        finally
        {
            stack.Remove(path);
        }
    }

    private static IEnumerable<string> ResolveIncludes(string value, string baseDirectory)
    {
        var result = new List<string>();
        foreach (var raw in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = raw.Trim('"');
            if (pattern == "~")
                pattern = AppPaths.Home;
            else if (pattern.StartsWith("~/", StringComparison.Ordinal))
                pattern = Path.Combine(AppPaths.Home, pattern[2..]);
            if (!Path.IsPathRooted(pattern))
                pattern = Path.Combine(baseDirectory, pattern);

            var directory = Path.GetDirectoryName(pattern);
            var name = Path.GetFileName(pattern);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name))
                continue;

            if (name.IndexOfAny(['*', '?']) < 0)
            {
                if (File.Exists(pattern))
                    result.Add(Path.GetFullPath(pattern));
                continue;
            }

            try
            {
                result.AddRange(Directory.EnumerateFiles(directory, name)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(Path.GetFullPath));
            }
            catch
            {
                // A missing include directory is not an error.
            }
        }

        return result;
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
