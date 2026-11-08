using System.Diagnostics;
using System.Globalization;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

/// <summary>
/// Optional PDF rendering through poppler's pdftoppm/pdfinfo. Nothing is
/// bundled; when the tools are absent Ordoa falls back to a metadata-only view.
/// </summary>
public static class PdfPreviewTool
{
    public static Task<byte[]?> ExtractFirstPageAsync(string path, CancellationToken cancellationToken)
        => RenderPageAsync(path, 1, 1600, cancellationToken);

    public static async Task<byte[]?> RenderPageAsync(string path, int pageNumber, int maxWidth,
        CancellationToken cancellationToken)
    {
        var configured = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ORDOA_PDFTOPPM"),
            Environment.GetEnvironmentVariable("PDFTOPPM_PATH"),
            "pdftoppm");
        var tool = configured is null ? null : FindExecutable(configured);
        if (tool is null)
            return null;

        pageNumber = Math.Max(1, pageNumber);
        maxWidth = Math.Clamp(maxWidth, 320, 4096);
        var prefix = Path.Combine(Path.GetTempPath(), "ordoa-pdf-" + Guid.NewGuid().ToString("N"));
        var output = prefix + ".png";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var process = StartProcess(tool,
            [
                "-f", pageNumber.ToString(CultureInfo.InvariantCulture),
                "-l", pageNumber.ToString(CultureInfo.InvariantCulture),
                "-singlefile",
                "-png",
                "-scale-to", maxWidth.ToString(CultureInfo.InvariantCulture),
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

    /// <summary>Reads the page count with pdfinfo; returns 1 when poppler is unavailable.</summary>
    public static async Task<int> GetPageCountAsync(string path, CancellationToken cancellationToken)
    {
        var configured = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ORDOA_PDFINFO"),
            Environment.GetEnvironmentVariable("PDFINFO_PATH"),
            "pdfinfo");
        var tool = configured is null ? null : FindExecutable(configured);
        if (tool is null)
            return 1;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var process = StartProcess(tool, [path]);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await WaitForExitOrKillAsync(process, timeout.Token).ConfigureAwait(true);
            var text = await stdout.ConfigureAwait(true);
            _ = await stderr.ConfigureAwait(true);
            if (process.ExitCode != 0)
                return 1;

            foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith("Pages:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var value = line["Pages:".Length..].Trim();
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages) &&
                    pages > 0)
                {
                    return pages;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Return the safe default below.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"pdfinfo 读取页数失败: {path}", ex);
        }

        return 1;
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
