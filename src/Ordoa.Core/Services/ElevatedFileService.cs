using System.Diagnostics;
using System.Text;
using Ordoa.Core.Localization;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public sealed record ElevatedResult(bool Success, string Message);

/// <summary>
/// Optional file operations executed with administrator privileges. Linux uses
/// pkexec, macOS uses osascript with administrator privileges, and Windows uses
/// an elevated PowerShell process. Nothing runs unless the feature is enabled
/// and the user confirms the operation.
/// </summary>
public sealed class ElevatedFileService
{
    public bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    public bool IsAvailable => IsSupported && FindTool() is not null;

    public Task<ElevatedResult> DeleteAsync(IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
        => RunScriptAsync(ElevatedScriptBuilder.Delete(paths), cancellationToken);

    public Task<ElevatedResult> CopyAsync(IReadOnlyList<string> sources, string destination, bool move,
        CancellationToken cancellationToken = default)
        => RunScriptAsync(ElevatedScriptBuilder.Copy(sources, destination, move), cancellationToken);

    public Task<ElevatedResult> ChmodAsync(IReadOnlyList<string> paths, string mode,
        CancellationToken cancellationToken = default)
        => RunScriptAsync(ElevatedScriptBuilder.Chmod(mode, paths), cancellationToken);

    public Task<ElevatedResult> ChownAsync(IReadOnlyList<string> paths, string owner, string group,
        CancellationToken cancellationToken = default)
        => RunScriptAsync(ElevatedScriptBuilder.Chown(owner, group, paths), cancellationToken);

    private async Task<ElevatedResult> RunScriptAsync(string script, CancellationToken cancellationToken)
    {
        if (!IsSupported)
            return new ElevatedResult(false, L10n.T("当前平台不支持管理员操作。"));
        if (string.IsNullOrWhiteSpace(script))
            return new ElevatedResult(true, string.Empty);

        try
        {
            if (OperatingSystem.IsWindows())
                return await RunWindowsAsync(script, cancellationToken).ConfigureAwait(false);
            return await RunUnixAsync(script, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ElevatedResult(false, L10n.T("管理员操作已取消。"));
        }
        catch (Exception ex)
        {
            return new ElevatedResult(false, ex.Message);
        }
    }

    private async Task<ElevatedResult> RunUnixAsync(string script, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(AppPaths.CacheDirectory, "elevated");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "ordoa-" + Guid.NewGuid().ToString("N") + ".sh");
        try
        {
            await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\nset -e\n" + script,
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserExecute |
                                             UnixFileMode.UserWrite);

            if (OperatingSystem.IsMacOS())
            {
                var command = "/bin/sh " + ElevatedScriptBuilder.Quote(scriptPath);
                var appleScript = "do shell script \"" +
                                  ElevatedScriptBuilder.AppleScriptString(command) +
                                  "\" with administrator privileges";
                return await RunAsync("/usr/bin/osascript", ["-e", appleScript], cancellationToken)
                    .ConfigureAwait(false);
            }

            return await RunAsync("pkexec", ["/bin/sh", scriptPath], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* ignore */ }
        }
    }

    private async Task<ElevatedResult> RunWindowsAsync(string script, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var launcher = """
                       $p = Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile','-EncodedCommand','%ENCODED%' -Verb RunAs -PassThru -Wait
                       exit $p.ExitCode
                       """.Replace("%ENCODED%", encoded, StringComparison.Ordinal);
        var launcherEncoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(launcher));
        return await RunAsync("powershell.exe", ["-NoProfile", "-EncodedCommand", launcherEncoded],
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ElevatedResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(L10n.Format("无法启动 {0}", executable));
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode == 0)
            return new ElevatedResult(true, output.Trim());
        return new ElevatedResult(false, string.IsNullOrWhiteSpace(error)
            ? L10n.Format("管理员操作失败（退出码 {0}）", process.ExitCode)
            : error.Trim());
    }

    private static string? FindTool()
    {
        if (OperatingSystem.IsWindows())
            return FindExecutable("powershell.exe");
        if (OperatingSystem.IsMacOS())
            return FindExecutable("osascript");
        return FindExecutable("pkexec");
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
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
        return null;
    }
}
