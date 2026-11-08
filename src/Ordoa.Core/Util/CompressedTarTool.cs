using System.Diagnostics;
using Ordoa.Core.Localization;

namespace Ordoa.Core.Util;

/// <summary>Optional zstd/xz wrappers used for tar.zst and tar.xz archives.</summary>
public static class CompressedTarTool
{
    public enum Kind
    {
        Zstd,
        Xz,
    }

    public static bool IsAvailable(Kind kind) => Find(kind) is not null;

    public static async Task CompressAsync(Kind kind, string tarPath, string destination,
        CancellationToken cancellationToken)
    {
        var executable = Find(kind) ?? throw new NotSupportedException(
            kind == Kind.Zstd ? L10n.T("需要安装 zstd 命令行工具") : L10n.T("需要安装 xz 命令行工具"));

        if (kind == Kind.Zstd)
        {
            await RunAsync(executable,
                ["-q", "-T0", "-f", "-o", destination, tarPath], cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunAsync(executable,
            ["-T0", "-z", "-f", "-k", tarPath], cancellationToken).ConfigureAwait(false);
        var produced = tarPath + ".xz";
        if (!File.Exists(produced))
            throw new IOException(L10n.T("xz 没有生成压缩文件"));
        File.Move(produced, destination, overwrite: true);
    }

    public static async Task DecompressAsync(Kind kind, string archivePath, string tarPath,
        CancellationToken cancellationToken)
    {
        var executable = Find(kind) ?? throw new NotSupportedException(
            kind == Kind.Zstd ? L10n.T("需要安装 zstd 命令行工具") : L10n.T("需要安装 xz 命令行工具"));

        if (kind == Kind.Zstd)
        {
            await RunAsync(executable,
                ["-d", "-q", "-f", "-o", tarPath, archivePath], cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunAsync(executable, ["-T0", "-d", "-c", archivePath], cancellationToken, outputPath: tarPath)
            .ConfigureAwait(false);
    }

    private static async Task RunAsync(string executable, string[] arguments, CancellationToken cancellationToken,
        string? outputPath = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = outputPath is not null,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(L10n.Format("无法启动 {0}", executable));
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        if (outputPath is not null)
        {
            await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 256 * 1024, FileOptions.Asynchronous);
            await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(error)
                ? L10n.T("压缩或解压失败")
                : error.Trim());
    }

    private static string? Find(Kind kind)
    {
        var names = kind == Kind.Zstd ? new[] { "zstd" } : new[] { "xz" };
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
}
