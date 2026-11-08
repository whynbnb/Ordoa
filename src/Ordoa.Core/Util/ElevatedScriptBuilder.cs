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
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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

    /// <summary>Build a PowerShell script for Windows elevation.</summary>
    public static string WindowsScript(IEnumerable<string> lines)
        => string.Join(Environment.NewLine, lines);
}
