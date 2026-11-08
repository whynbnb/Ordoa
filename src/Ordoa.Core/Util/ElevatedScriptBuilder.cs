using System.Text;

namespace Ordoa.Core.Util;

/// <summary>Builds the shell scripts used by optional elevated file operations.</summary>
public static class ElevatedScriptBuilder
{
    public static string Delete(IEnumerable<string> paths)
        => string.Join(Environment.NewLine,
            paths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => "rm -rf -- " + Quote(path)));

    public static string Copy(IEnumerable<string> sources, string destination, bool move)
    {
        var builder = new StringBuilder();
        var command = move ? "mv" : "cp -a";
        builder.Append("mkdir -p -- ").Append(Quote(destination)).AppendLine();
        foreach (var source in sources.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var name = FileNameOf(source);
            if (string.IsNullOrEmpty(name))
                continue;
            var target = Path.Combine(destination, name);
            builder.Append(command).Append(" -- ").Append(Quote(source)).Append(' ')
                .Append(Quote(target)).AppendLine();
        }
        return builder.ToString();
    }

    public static string Chmod(string mode, IEnumerable<string> paths)
    {
        var builder = new StringBuilder();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
            builder.Append("chmod ").Append(mode).Append(" -- ").Append(Quote(path)).AppendLine();
        return builder.ToString();
    }

    public static string Chown(string owner, string group, IEnumerable<string> paths)
    {
        var builder = new StringBuilder();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
            builder.Append("chown -- ").Append(Quote(owner + ":" + group)).Append(' ')
                .Append(Quote(path)).AppendLine();
        return builder.ToString();
    }

    /// <summary>Single-quote a POSIX shell argument.</summary>
    public static string Quote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Escape a value embedded in an AppleScript string literal.</summary>
    public static string AppleScriptString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    public static string WindowsDelete(IEnumerable<string> paths)
        => string.Join(Environment.NewLine,
            paths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => "Remove-Item -LiteralPath " + PsQuote(path) +
                                " -Recurse -Force -ErrorAction Stop"));

    public static string WindowsCopy(IEnumerable<string> sources, string destination, bool move)
    {
        var builder = new StringBuilder();
        builder.Append("New-Item -ItemType Directory -Force -Path ")
            .Append(PsQuote(destination)).AppendLine(" | Out-Null");
        var command = move ? "Move-Item" : "Copy-Item";
        foreach (var source in sources.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var name = FileNameOf(source);
            if (string.IsNullOrEmpty(name))
                continue;
            var target = Path.Combine(destination, name);
            builder.Append(command).Append(" -LiteralPath ").Append(PsQuote(source))
                .Append(" -Destination ").Append(PsQuote(target));
            if (!move)
                builder.Append(" -Recurse");
            builder.AppendLine(" -Force -ErrorAction Stop");
        }
        return builder.ToString();
    }

    public static string PsQuote(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string FileNameOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var separator = trimmed.LastIndexOfAny(['/', '\\']);
        return separator < 0 ? trimmed : trimmed[(separator + 1)..];
    }
}
