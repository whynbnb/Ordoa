using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ordoa.Core.Localization;

namespace Ordoa.Core.Util;

/// <summary>
/// Minimal wrapper around the 7-Zip command-line tool. 7-Zip is optional: when
/// it is missing, 7z and rar archives are reported as unsupported while zip,
/// tar and tar.gz keep using the built-in implementation.
/// </summary>
public static class SevenZipTool
{
    private static readonly Lazy<string?> ExecutablePath = new(FindExecutable);

    public static bool IsAvailable => ExecutablePath.Value is not null;

    public static string? Executable => ExecutablePath.Value;

    public sealed record Entry(string Path, long Size, DateTimeOffset? Modified, bool IsDirectory, bool IsEncrypted);

    public static IReadOnlyList<Entry> List(string archivePath, CancellationToken cancellationToken,
        string? password = null)
    {
        var output = Run(null, ["l", "-slt", "-ba", "-bd", PasswordArgument(password), "--", archivePath],
            cancellationToken);
        var entries = new List<Entry>();
        string? path = null;
        long size = 0;
        DateTimeOffset? modified = null;
        var isDirectory = false;
        var encrypted = false;

        void Flush()
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            entries.Add(new Entry(path.Replace('\\', '/').TrimStart('/'), size, modified,
                isDirectory || path.EndsWith('/') || path.EndsWith('\\'), encrypted));
            path = null;
            size = 0;
            modified = null;
            isDirectory = false;
            encrypted = false;
        }

        foreach (var raw in output.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0)
                continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "Path":
                    path = value;
                    break;
                case "Size" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    size = parsed;
                    break;
                case "Modified" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var date):
                    modified = date;
                    break;
                case "Attributes" when value.StartsWith('D'):
                    isDirectory = true;
                    break;
                case "Folder" when value == "+":
                    isDirectory = true;
                    break;
                case "Encrypted" when value == "+":
                    encrypted = true;
                    break;
            }
        }

        Flush();
        return entries;
    }

    public static void Create(string archivePath, IReadOnlyList<string> paths, int level, int splitMb,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "a",
            "-t7z",
            "-mx=" + Math.Clamp(level, 0, 9).ToString(CultureInfo.InvariantCulture),
            "-bd",
        };
        if (splitMb > 0)
            arguments.Add("-v" + splitMb.ToString(CultureInfo.InvariantCulture) + "m");
        arguments.Add("--");
        arguments.Add(archivePath);
        arguments.AddRange(paths);
        _ = Run(null, arguments, cancellationToken);
    }

    public static void ExtractEntry(string archivePath, string entryPath, string destination,
        CancellationToken cancellationToken, string? password = null)
    {
        var executable = Executable ?? throw new NotSupportedException(L10n.T("需要安装 7z 命令行工具才能读取此压缩包"));
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "x", "-so", "-y", "-bd", PasswordArgument(password), "--", archivePath, entryPath })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException(L10n.T("无法启动 7z"));
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            128 * 1024);
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken).GetAwaiter().GetResult();
            process.WaitForExit();
        }
        catch
        {
            try { process.Kill(true); } catch { /* ignore */ }
            throw;
        }

        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            TryDelete(destination);
            throw new IOException(string.IsNullOrWhiteSpace(error)
                ? L10n.T("7z 解压失败，压缩包可能需要密码或已经损坏")
                : error.Trim());
        }
    }

    public static void Add(string archivePath, string workingDirectory, IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken, string? password = null)
    {
        var arguments = new List<string> { "a", "-y", "-bd" };
        if (!string.IsNullOrEmpty(password))
            arguments.Add("-p" + password);
        arguments.Add("--");
        arguments.Add(archivePath);
        arguments.AddRange(relativePaths);
        _ = Run(workingDirectory, arguments, cancellationToken);
    }

    public static void Delete(string archivePath, IReadOnlyList<string> entryPaths,
        CancellationToken cancellationToken, string? password = null)
    {
        var arguments = new List<string> { "d", "-y", "-bd" };
        if (!string.IsNullOrEmpty(password))
            arguments.Add("-p" + password);
        arguments.Add("--");
        arguments.Add(archivePath);
        foreach (var entry in entryPaths)
        {
            var clean = entry.Replace('\\', '/').TrimStart('/');
            if (clean.EndsWith("/*", StringComparison.Ordinal))
                arguments.Add(clean[..^2]);
            arguments.Add(clean);
        }
        _ = Run(null, arguments, cancellationToken);
    }

    public static void Rename(string archivePath, string oldPath, string newPath,
        CancellationToken cancellationToken, string? password = null)
    {
        var arguments = new List<string> { "rn", "-y", "-bd" };
        if (!string.IsNullOrEmpty(password))
            arguments.Add("-p" + password);
        arguments.Add("--");
        arguments.Add(archivePath);
        arguments.Add(oldPath.Replace('\\', '/').TrimStart('/'));
        arguments.Add(newPath.Replace('\\', '/').TrimStart('/'));
        _ = Run(null, arguments, cancellationToken);
    }

    public static void Extract(string archivePath, string destination,
        IReadOnlyList<string>? entryPaths, CancellationToken cancellationToken, string? password = null)
    {
        var executable = Executable ?? throw new NotSupportedException(L10n.T("需要安装 7z 命令行工具才能读取此压缩包"));
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("x");
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add("-bd");
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add("-o" + destination);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(archivePath);
        if (entryPaths is not null)
        {
            foreach (var entry in entryPaths)
                start.ArgumentList.Add(entry.Replace('\\', '/').TrimStart('/'));
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException(L10n.T("无法启动 7z"));
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            process.WaitForExit();
        }
        catch
        {
            try { process.Kill(true); } catch { /* ignore */ }
            throw;
        }

        var error = errorTask.GetAwaiter().GetResult();
        _ = outputTask.GetAwaiter().GetResult();
        if (process.ExitCode is not 0 and not 1)
            throw new IOException(string.IsNullOrWhiteSpace(error)
                ? L10n.T("7z 解压失败，压缩包可能需要密码或已经损坏")
                : error.Trim());
    }

    private static string Run(string? workingDirectory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var executable = Executable ?? throw new NotSupportedException(L10n.T("需要安装 7z 命令行工具才能读取此压缩包"));
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException(L10n.T("无法启动 7z"));
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            process.WaitForExit();
        }
        catch
        {
            try { process.Kill(true); } catch { /* ignore */ }
            throw;
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(error) ? L10n.T("7z 读取失败") : error.Trim());
        return output;
    }

    private static string PasswordArgument(string? password)
        => string.IsNullOrEmpty(password) ? "-p-" : "-p" + password;

    private static string? FindExecutable()
    {
        string[] names = OperatingSystem.IsWindows() ? ["7z.exe", "7za.exe"] : ["7z", "7zz", "7za"];
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }
}
