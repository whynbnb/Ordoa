using System.Text;
using Ordoa.Core.Localization;

namespace Ordoa.Core.Services;

/// <summary>A freedesktop.org desktop entry that can open files.</summary>
public sealed record DesktopApplication(
    string Id,
    string Name,
    string Comment,
    string Exec,
    string? TryExec,
    string? Icon,
    IReadOnlySet<string> MimeTypes,
    bool Terminal,
    bool NoDisplay);

public static class DesktopEntryService
{
    public static DesktopApplication? Parse(string filePath, string id)
    {
        try
        {
            var values = ReadDesktopGroup(filePath);
            if (values is null)
                return null;

            if (GetBool(values, "Hidden"))
                return null;

            if (values.TryGetValue("Type", out var type) &&
                !string.Equals(type, "Application", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var exec = Get(values, "Exec");
            if (string.IsNullOrWhiteSpace(exec))
                return null;

            var name = Localized(values, "Name");
            if (string.IsNullOrWhiteSpace(name))
                name = Path.GetFileNameWithoutExtension(id);

            var mimeTypes = Get(values, "MimeType")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(mime => mime.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);

            return new DesktopApplication(
                id,
                name.Trim(),
                Localized(values, "Comment").Trim(),
                exec.Trim(),
                NullIfEmpty(Get(values, "TryExec")),
                NullIfEmpty(Get(values, "Icon")),
                mimeTypes,
                GetBool(values, "Terminal"),
                GetBool(values, "NoDisplay"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Builds the process arguments for a single file, expanding desktop entry field codes.</summary>
    public static IReadOnlyList<string> BuildArguments(string exec, string filePath)
    {
        var tokens = Tokenize(exec);
        var result = new List<string>(tokens.Count + 1);
        var hadFileField = false;
        var uri = ToFileUri(filePath);

        foreach (var token in tokens)
        {
            if (token == "%i")
            {
                if (result.Count > 0 && result[^1] == "--icon")
                    result.RemoveAt(result.Count - 1);
                continue;
            }

            var expanded = ExpandToken(token, filePath, uri, ref hadFileField);
            if (expanded.Length > 0)
                result.Add(expanded);
        }

        if (!hadFileField && result.Count > 0)
            result.Add(filePath);

        return result;
    }

    public static bool IsExecutableAvailable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return true;

        executable = executable.Trim().Trim('"');
        if (executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(executable);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, executable)))
                    return true;
            }
            catch { /* ignore invalid PATH entries */ }
        }

        return false;
    }

    private static string ExpandToken(string token, string filePath, string uri, ref bool hadFileField)
    {
        if (!token.Contains('%'))
            return token;

        var builder = new StringBuilder(token.Length + 32);
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] != '%' || i + 1 >= token.Length)
            {
                builder.Append(token[i]);
                continue;
            }

            var code = token[++i];
            switch (code)
            {
                case '%':
                    builder.Append('%');
                    break;
                case 'f':
                case 'F':
                    builder.Append(filePath);
                    hadFileField = true;
                    break;
                case 'u':
                case 'U':
                    builder.Append(uri);
                    hadFileField = true;
                    break;
                case 'i':
                case 'c':
                case 'k':
                case 'd':
                case 'D':
                case 'n':
                case 'N':
                case 'v':
                case 'm':
                    break;
                default:
                    builder.Append('%').Append(code);
                    break;
            }
        }

        return builder.ToString();
    }

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\\' && i + 1 < value.Length)
            {
                builder.Append(value[++i]);
                hasToken = true;
            }
            else if (ch == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (hasToken)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                    hasToken = false;
                }
            }
            else
            {
                builder.Append(ch);
                hasToken = true;
            }
        }

        if (hasToken)
            tokens.Add(builder.ToString());

        return tokens;
    }

    private static string ToFileUri(string filePath)
    {
        try
        {
            return new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
        }
        catch
        {
            return filePath;
        }
    }

    private static string Localized(IReadOnlyDictionary<string, string> values, string key)
    {
        if (L10n.IsChinese)
        {
            foreach (var candidate in new[] { key + "[zh_CN]", key + "[zh]", key })
            {
                if (values.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value))
                    return Unescape(value);
            }
        }
        else
        {
            foreach (var candidate in new[] { key + "[en_US]", key + "[en]", key })
            {
                if (values.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value))
                    return Unescape(value);
            }
        }

        return values.TryGetValue(key, out var fallback) ? Unescape(fallback) : string.Empty;
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? Unescape(value) : string.Empty;

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key)
        => bool.TryParse(Get(values, key), out var value) && value;

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Unescape(string value)
        => value
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\s", " ", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, string>? ReadDesktopGroup(string filePath)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var inGroup = false;
        foreach (var raw in File.ReadLines(filePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inGroup = string.Equals(line, "[Desktop Entry]", StringComparison.Ordinal);
                continue;
            }

            if (!inGroup)
                continue;

            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            values[key] = value;
        }

        return values.Count == 0 ? null : values;
    }
}
