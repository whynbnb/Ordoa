using Ordoa.Core.Models;

namespace Ordoa.Core.Services;

/// <summary>Finds terminal emulators installed on this machine.</summary>
public static class TerminalCatalog
{
    public static IReadOnlyList<TerminalProfile> Discover()
    {
        var profiles = new List<TerminalProfile>();
        if (OperatingSystem.IsWindows())
            AddWindows(profiles);
        else if (OperatingSystem.IsMacOS())
            AddMacOS(profiles);
        else
            AddLinux(profiles);

        return profiles;
    }

    /// <summary>Finds the Herdr terminal multiplexer when it is installed.</summary>
    public static TerminalProfile? FindHerdr()
    {
        var executable = FindExecutable("herdr");
        return executable is null
            ? null
            : new TerminalProfile("herdr", "Herdr", executable, string.Empty, "%c", IsHerdr: true);
    }

    public static string? FindExecutable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        name = name.Trim().Trim('"');
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(name) ? name : null;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var candidateName in CandidateNames(name))
                {
                    try
                    {
                        var candidate = Path.Combine(directory, candidateName);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch
                    {
                        // Ignore malformed PATH entries.
                    }
                }
            }
        }

        // Common user-local install locations that some sessions omit from PATH.
        // Homebrew on Apple Silicon is not part of the default GUI session PATH.
        foreach (var directory in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
                     "/opt/homebrew/bin",
                     "/opt/homebrew/sbin",
                     "/opt/local/bin",
                     "/usr/local/bin",
                     "/usr/local/sbin",
                 })
        {
            foreach (var candidateName in CandidateNames(name))
            {
                try
                {
                    var candidate = Path.Combine(directory, candidateName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Ignore inaccessible directories.
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateNames(string name)
    {
        yield return name;
        if (!OperatingSystem.IsWindows() || Path.HasExtension(name))
            yield break;

        foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            yield return name + extension;
    }

    private static void AddLinux(List<TerminalProfile> profiles)
    {
        Add(profiles, "xdg-terminal-exec", "xdg-terminal-exec",
            "--working-directory=%d", "--working-directory=%d -- sh -lc %c");
        Add(profiles, "kgx", "GNOME Console",
            "--working-directory=%d", "--working-directory=%d -- sh -lc %c");
        Add(profiles, "gnome-terminal", "GNOME Terminal",
            "--working-directory=%d", "--working-directory=%d -- sh -lc %c");
        Add(profiles, "konsole", "Konsole",
            "--workdir %d", "--workdir %d -e sh -lc %c");
        Add(profiles, "xfce4-terminal", "Xfce Terminal",
            "--working-directory=%d", "--working-directory=%d -x sh -lc %c");
        Add(profiles, "alacritty", "Alacritty",
            "--working-directory %d", "--working-directory %d -e sh -lc %c");
        Add(profiles, "kitty", "Kitty",
            "--directory %d", "--directory %d sh -lc %c");
        Add(profiles, "wezterm", "WezTerm",
            "start --cwd %d", "start --cwd %d -- sh -lc %c");
        Add(profiles, "foot", "Foot",
            "--working-directory=%d", "--working-directory=%d sh -lc %c");
        Add(profiles, "ghostty", "Ghostty",
            "--working-directory=%d", "--working-directory=%d -e sh -lc %c");
        Add(profiles, "terminator", "Terminator",
            "--working-directory=%d", "--working-directory=%d -x sh -lc %c");
        Add(profiles, "tilix", "Tilix",
            "--working-directory=%d", "--working-directory=%d -e sh -lc %c");
        Add(profiles, "mate-terminal", "MATE Terminal",
            "--working-directory=%d", "--working-directory=%d -- sh -lc %c");
        Add(profiles, "lxterminal", "LXTerminal",
            "--working-directory=%d", "--working-directory=%d -e sh -lc %c");
        Add(profiles, "qterminal", "QTerminal",
            "--workdir %d", "--workdir %d -e sh -lc %c");
        Add(profiles, "sakura", "Sakura",
            "--working-directory=%d", "--working-directory=%d -e sh -lc %c");
        Add(profiles, "terminology", "Terminology",
            "--working-directory=%d", "--working-directory=%d -e sh -lc %c");
        Add(profiles, "st", "st",
            "-d %d", "-d %d -e sh -lc %c");
        Add(profiles, "urxvt", "URxvt",
            "-cd %d", "-cd %d -e sh -lc %c");
        Add(profiles, "xterm", "XTerm",
            string.Empty, "-e sh -lc %c");
    }

    private static void AddMacOS(List<TerminalProfile> profiles)
    {
        // Prefer command-capable CLI binaries when they are installed, then
        // fall back to the .app bundle so Finder-launched sessions (with a
        // minimal PATH) still find the terminal.
        Add(profiles, "wezterm", "WezTerm",
            "start --cwd %d", "start --cwd %d -- sh -lc %c");
        Add(profiles, "kitty", "Kitty",
            "--directory %d", "--directory %d sh -lc %c");
        Add(profiles, "alacritty", "Alacritty",
            "--working-directory %d", "--working-directory %d -e sh -lc %c");
        Add(profiles, "rio", "Rio",
            "-w %d", "-w %d -e sh -lc %c");

        AddMacApp(profiles, "app:terminal", "Terminal", "Terminal");
        AddMacApp(profiles, "app:iterm", "iTerm2", "iTerm", null, "iTerm", "iTerm2");
        AddMacApp(profiles, "app:wezterm", "WezTerm", "WezTerm");
        AddMacApp(profiles, "app:kitty", "Kitty", "kitty");
        AddMacApp(profiles, "app:alacritty", "Alacritty", "Alacritty");
        AddMacApp(profiles, "app:ghostty", "Ghostty", "Ghostty",
            "-n -a \"{0}\" --args --working-directory=%d");
        AddMacApp(profiles, "app:warp", "Warp", "Warp");
        AddMacApp(profiles, "app:tabby", "Tabby", "Tabby");
        AddMacApp(profiles, "app:hyper", "Hyper", "Hyper");
        AddMacApp(profiles, "app:rio", "Rio", "rio", null, "rio", "Rio");
        AddMacApp(profiles, "app:macterm", "MacTerm", "MacTerm");
    }

    private static void AddMacApp(List<TerminalProfile> profiles, string id, string name,
        string appName, string? directoryArguments = null, params string[] bundleNames)
    {
        if (profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
            return;

        var appPath = FindMacApp(bundleNames.Length == 0 ? [appName] : bundleNames);
        if (appPath is null)
            return;

        var arguments = directoryArguments is null
            ? $"-a \"{appPath}\" %d"
            : string.Format(directoryArguments, appPath);
        profiles.Add(new TerminalProfile(id, name, "/usr/bin/open", arguments, string.Empty));
    }

    private static string? FindMacApp(IReadOnlyList<string> bundleNames)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            "/Applications",
            "/Applications/Utilities",
            Path.Combine(home, "Applications"),
            "/System/Applications",
            "/System/Applications/Utilities",
        };

        foreach (var root in roots)
        {
            foreach (var bundleName in bundleNames)
            {
                try
                {
                    var path = Path.Combine(root, bundleName + ".app");
                    if (Directory.Exists(path))
                        return path;
                }
                catch
                {
                    // Ignore malformed or inaccessible paths.
                }
            }
        }

        return null;
    }

    private static void AddWindows(List<TerminalProfile> profiles)
    {
        Add(profiles, "wt.exe", "Windows Terminal",
            "-d %d", "-d %d cmd /c %c");
        Add(profiles, "pwsh.exe", "PowerShell",
            "-NoExit -Command Set-Location -LiteralPath %d", "-NoExit -Command %c");
        Add(profiles, "powershell.exe", "Windows PowerShell",
            "-NoExit -Command Set-Location -LiteralPath %d", "-NoExit -Command %c");
        Add(profiles, "cmd.exe", "Command Prompt",
            "/K cd /d %d", "/K %c");
    }

    private static void Add(List<TerminalProfile> profiles, string executable, string name,
        string directoryArguments, string commandArguments)
    {
        var path = FindExecutable(executable);
        if (path is null)
            return;

        var id = "exe:" + Path.GetFileName(executable).ToLowerInvariant();
        if (profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.Ordinal)))
            return;

        profiles.Add(new TerminalProfile(id, name, path, directoryArguments, commandArguments));
    }
}
