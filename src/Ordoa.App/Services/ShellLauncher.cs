using System.Diagnostics;
using System.Runtime.InteropServices;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

public sealed class ShellLauncher
{
    public Task OpenAsync(string path)
    {
        return Task.Run(() =>
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            };
            Process.Start(startInfo);
        });
    }

    public Task<bool> OpenTerminalAsync(string directory)
    {
        return Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var windowsTerminal = FindExecutable("wt.exe") ?? FindExecutable("wt");
                if (windowsTerminal is not null)
                    return TryStart(windowsTerminal, ["-d", directory]);
                return TryStart("cmd.exe", ["/c", "start", "cmd.exe", "/K", $"cd /d \"{directory}\""]);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return TryStart("open", ["-a", "Terminal", directory]);

            var envTerminal = Environment.GetEnvironmentVariable("TERMINAL");
            var candidates = new List<(string Executable, string[] Arguments)>();
            if (!string.IsNullOrWhiteSpace(envTerminal))
                candidates.Add((envTerminal, ["--working-directory", directory]));

            candidates.AddRange(
            [
                ("xdg-terminal-exec", ["--working-directory", directory]),
                ("kgx", ["--working-directory", directory]),
                ("gnome-terminal", [$"--working-directory={directory}"]),
                ("konsole", ["--workdir", directory]),
                ("xfce4-terminal", [$"--working-directory={directory}"]),
                ("alacritty", ["--working-directory", directory]),
                ("kitty", ["--directory", directory]),
                ("wezterm", ["start", "--cwd", directory]),
                ("foot", [$"--working-directory={directory}"]),
                ("terminator", ["--working-directory", directory]),
                ("xterm", ["-e", "sh", "-lc", $"cd {Quote(directory)} && exec $SHELL"]),
            ]);

            foreach (var (executable, arguments) in candidates)
            {
                if (FindExecutable(executable) is null)
                    continue;
                if (TryStart(executable, arguments))
                    return true;
            }

            return false;
        });
    }

    public bool RevealSupported => !RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private static bool TryStart(string executable, string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            Core.Services.Log.Warn($"启动终端失败: {executable}", ex);
            return false;
        }
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
            catch { /* ignore */ }
        }
        return null;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
