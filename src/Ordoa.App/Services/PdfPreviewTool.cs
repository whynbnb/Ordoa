using System.Diagnostics;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

/// <summary>
/// Optional first-page PDF rendering through poppler's pdftoppm. Nothing is
/// bundled; when the tool is absent Ordoa falls back to a metadata-only view.
/// </summary>
public static class PdfPreviewTool
{
    public static async Task<byte[]?> ExtractFirstPageAsync(string path, CancellationToken cancellationToken)
    {
        var configured = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ORDOA_PDFTOPPM"),
            Environment.GetEnvironmentVariable("PDFTOPPM_PATH"),
            "pdftoppm");
        var tool = configured is null ? null : FindExecutable(configured);
        if (tool is null)
            return null;

        var prefix = Path.Combine(Path.GetTempPath(), "ordoa-pdf-" + Guid.NewGuid().ToString("N"));
        var output = prefix + ".png";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var process = StartProcess(tool,
            [
                "-f", "1",
                "-l", "1",
                "-singlefile",
                "-png",
                "-scale-to", "1600",
                path,
                prefix,
            ]);

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await WaitForExitOrKillAsync(process, timeout.Token).ConfigureAwait(true);
            _ = await stdout.ConfigureAwait(true);
            _ = await stderr.ConfigureAwait(true);

            if (process.ExitCode != 0 || !File.Exists(output))
                return null;
            return await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"pdftoppm 渲染失败: {path}", ex);
            return null;
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { /* ignore */ }
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static async Task WaitForExitOrKillAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }
    }

    private static Process StartProcess(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"无法启动 {executable}");
        return process;
    }

    private static string? FindExecutable(string name)
    {
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(name) ? name : null;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { /* ignore malformed PATH entry */ }
        }
        return null;
    }
}
