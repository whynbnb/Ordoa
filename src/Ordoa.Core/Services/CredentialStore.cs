using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public interface ICredentialStore
{
    Task<string?> GetPasswordAsync(string connectionId, CancellationToken cancellationToken = default);
    Task SetPasswordAsync(string connectionId, string password, CancellationToken cancellationToken = default);
    Task DeletePasswordAsync(string connectionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores passwords in the desktop keyring when available (libsecret on Linux,
/// macOS Keychain) and falls back to a 0600 JSON file otherwise.
/// </summary>
public sealed class CredentialStore : ICredentialStore
{
    private const string ServiceName = "Ordoa";
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly bool _useSecretTool;
    private readonly bool _useMacKeychain;

    public CredentialStore()
    {
        _useSecretTool = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && FindExecutable("secret-tool") is not null;
        _useMacKeychain = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && FindExecutable("security") is not null;
    }

    public async Task<string?> GetPasswordAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        if (_useSecretTool)
        {
            var result = await RunAsync("secret-tool",
                ["lookup", "application", "ordoa", "connection", connectionId], null, cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode == 0)
                return result.StandardOutput.TrimEnd('\r', '\n');
        }

        if (_useMacKeychain)
        {
            var result = await RunAsync("security",
                ["find-generic-password", "-s", ServiceName, "-a", connectionId, "-w"], null, cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode == 0)
                return result.StandardOutput.TrimEnd('\r', '\n');
        }

        return await ReadFallbackAsync(connectionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPasswordAsync(string connectionId, string password, CancellationToken cancellationToken = default)
    {
        if (_useSecretTool)
        {
            var result = await RunAsync("secret-tool",
                ["store", "--label", $"Ordoa · {connectionId}", "application", "ordoa", "connection", connectionId],
                password, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                await RemoveFallbackAsync(connectionId, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (_useMacKeychain)
        {
            var result = await RunAsync("security",
                ["add-generic-password", "-s", ServiceName, "-a", connectionId, "-w", password, "-U"],
                null, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                await RemoveFallbackAsync(connectionId, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await WriteFallbackAsync(connectionId, password, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePasswordAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        if (_useSecretTool)
        {
            await RunAsync("secret-tool",
                ["clear", "application", "ordoa", "connection", connectionId], null, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_useMacKeychain)
        {
            await RunAsync("security",
                ["delete-generic-password", "-s", ServiceName, "-a", connectionId], null, cancellationToken)
                .ConfigureAwait(false);
        }

        await RemoveFallbackAsync(connectionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadFallbackAsync(string connectionId, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = ReadFallbackFile();
            return map.TryGetValue(connectionId, out var encoded) ? Decode(encoded) : null;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private async Task WriteFallbackAsync(string connectionId, string password, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = ReadFallbackFile();
            map[connectionId] = Encode(password);
            WriteFallbackFile(map);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private async Task RemoveFallbackAsync(string connectionId, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = ReadFallbackFile();
            if (map.Remove(connectionId))
                WriteFallbackFile(map);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private static Dictionary<string, string> ReadFallbackFile()
    {
        try
        {
            if (!File.Exists(AppPaths.CredentialsFile))
                return new Dictionary<string, string>(StringComparer.Ordinal);
            var json = File.ReadAllText(AppPaths.CredentialsFile);
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.DictionaryStringString) ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void WriteFallbackFile(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        var json = JsonSerializer.Serialize(map, CoreJsonContext.Default.DictionaryStringString);
        var temp = AppPaths.CredentialsFile + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* ignore */ }
        }
        File.Move(temp, AppPaths.CredentialsFile, true);
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Decode(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch { return string.Empty; }
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
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { /* ignore */ }
        }
        return null;
    }

    private static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? stdin,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new ProcessResult(-1, string.Empty, "无法启动进程");

            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            Log.Warn($"密钥环命令失败: {fileName}", ex);
            return new ProcessResult(-1, string.Empty, ex.Message);
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
