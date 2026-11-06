using System.Text;

namespace Ordoa.Core.Services;

/// <summary>
/// Expands file-manager placeholders for custom commands and splits terminal
/// argument templates into real process arguments.
/// </summary>
public static class CommandTemplateService
{
    /// <summary>
    /// True when the template uses a placeholder that only makes sense with a
    /// selection (%f, %F, %n, %N or %p).
    /// </summary>
    public static bool NeedsSelection(string? template)
    {
        if (string.IsNullOrEmpty(template))
            return false;

        for (var i = 0; i < template.Length - 1; i++)
        {
            if (template[i] != '%')
                continue;

            if (template[i + 1] is 'f' or 'F' or 'n' or 'N' or 'p')
                return true;
            i++;
        }

        return false;
    }

    /// <summary>
    /// Replaces placeholders in a shell command line. When <paramref name="quote"/>
    /// is true every substituted value is quoted for the current shell, so paths
    /// containing spaces work without additional quoting in the template.
    /// </summary>
    public static string Expand(string? template, string workingDirectory,
        IReadOnlyList<string> selectedPaths, bool quote)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        var firstPath = selectedPaths.Count > 0 ? selectedPaths[0] : string.Empty;
        var firstParent = firstPath.Length > 0 ? Path.GetDirectoryName(firstPath) ?? workingDirectory : workingDirectory;
        var firstName = firstPath.Length > 0 ? Path.GetFileName(firstPath) : string.Empty;

        var builder = new StringBuilder(template.Length + 32);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '%' || i + 1 >= template.Length)
            {
                builder.Append(template[i]);
                continue;
            }

            var code = template[++i];
            switch (code)
            {
                case '%':
                    builder.Append('%');
                    break;
                case 'f':
                    AppendValue(builder, firstPath, quote);
                    break;
                case 'F':
                    AppendList(builder, selectedPaths, quote);
                    break;
                case 'n':
                    AppendValue(builder, firstName, quote);
                    break;
                case 'N':
                    AppendList(builder, selectedPaths.Select(SafeFileName).ToList(), quote);
                    break;
                case 'd':
                    AppendValue(builder, workingDirectory, quote);
                    break;
                case 'p':
                    AppendValue(builder, firstParent, quote);
                    break;
                default:
                    builder.Append('%').Append(code);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits a terminal argument template. <c>%d</c> expands to the working
    /// directory and <c>%c</c> to one complete command argument; both stay in
    /// the same token even when they contain spaces.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string? template, string workingDirectory,
        string? command)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(template))
            return tokens;

        var builder = new StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var hasToken = false;

        void Flush()
        {
            if (!hasToken)
                return;
            tokens.Add(builder.ToString());
            builder.Clear();
            hasToken = false;
        }

        for (var i = 0; i < template.Length; i++)
        {
            var ch = template[i];
            if (ch == '\\' && i + 1 < template.Length && !inSingleQuote)
            {
                builder.Append(template[++i]);
                hasToken = true;
            }
            else if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                hasToken = true;
            }
            else if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(ch) && !inSingleQuote && !inDoubleQuote)
            {
                Flush();
            }
            else if (ch == '%' && i + 1 < template.Length)
            {
                var code = template[++i];
                switch (code)
                {
                    case '%':
                        builder.Append('%');
                        break;
                    case 'd':
                        builder.Append(workingDirectory);
                        break;
                    case 'c':
                        builder.Append(command);
                        break;
                    default:
                        builder.Append('%').Append(code);
                        break;
                }

                hasToken = true;
            }
            else
            {
                builder.Append(ch);
                hasToken = true;
            }
        }

        Flush();
        return tokens;
    }

    /// <summary>Quotes a value for the shell in use on the current platform.</summary>
    public static string Quote(string value)
        => OperatingSystem.IsWindows() ? QuoteWindows(value) : QuotePosix(value);

    public static string QuotePosix(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public static string QuoteWindows(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void AppendValue(StringBuilder builder, string value, bool quote)
    {
        if (value.Length == 0)
            return;
        builder.Append(quote ? Quote(value) : value);
    }

    private static void AppendList(StringBuilder builder, IReadOnlyList<string> values, bool quote)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.IsNullOrEmpty(values[i]))
                continue;
            if (builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');
            builder.Append(quote ? Quote(values[i]) : values[i]);
        }
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return path;
        }
    }
}
