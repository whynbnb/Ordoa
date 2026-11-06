using System.Diagnostics;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

/// <summary>Discovers, configures and launches terminal emulators.</summary>
public sealed class TerminalService
{
    private readonly AppServices _services;

    public TerminalService(AppServices services)
    {
        _services = services;
    }

    public IReadOnlyList<TerminalProfile> Discover() => TerminalCatalog.Discover();

    public TerminalProfile? FindHerdr() => TerminalCatalog.FindHerdr();

    public TerminalProfile? GetDefaultProfile()
    {
        var settings = _services.Settings.Current;
        if (settings.TerminalMode == TerminalDetectionMode.Manual)
            return BuildManualProfile(settings);

        var profiles = Discover();
        if (profiles.Count == 0)
            return null;

        var defaultId = settings.TerminalPreferences
            .FirstOrDefault(preference => preference.IsDefault)?.Id;
        return profiles.FirstOrDefault(profile =>
                   string.Equals(profile.Id, defaultId, StringComparison.Ordinal)) ??
               profiles[0];
    }

    /// <summary>Terminals explicitly enabled for the "open with" submenu.</summary>
    public IReadOnlyList<TerminalProfile> GetContextMenuProfiles()
    {
        var settings = _services.Settings.Current;
        if (settings.TerminalMode == TerminalDetectionMode.Manual)
            return [];

        var preferences = new Dictionary<string, TerminalPreference>(StringComparer.Ordinal);
        foreach (var preference in settings.TerminalPreferences)
            preferences[preference.Id] = preference;

        return Discover()
            .Where(profile => preferences.TryGetValue(profile.Id, out var preference)
                ? preference.ShowInContextMenu
                : true)
            .ToList();
    }

    /// <summary>The default terminal, but only when its context-menu checkbox is enabled.</summary>
    public TerminalProfile? GetDefaultContextProfile()
    {
        var profile = GetDefaultProfile();
        if (profile is null)
            return null;
        if (profile.IsManual)
            return profile;

        var preference = _services.Settings.Current.TerminalPreferences
            .FirstOrDefault(item => string.Equals(item.Id, profile.Id, StringComparison.Ordinal));
        return preference is null || preference.ShowInContextMenu ? profile : null;
    }

    /// <summary>Herdr is only returned when the user has enabled its context-menu entry.</summary>
    public TerminalProfile? GetEnabledHerdr()
    {
        var preference = _services.Settings.Current.TerminalPreferences
            .FirstOrDefault(item => string.Equals(item.Id, "herdr", StringComparison.Ordinal));
        return preference?.ShowInContextMenu == true ? FindHerdr() : null;
    }

    public bool HasTerminal => GetDefaultProfile() is not null;

    public bool OpenDefault(string directory) => Open(GetDefaultProfile(), directory);

    public bool OpenById(string? terminalId, string directory)
    {
        if (string.IsNullOrWhiteSpace(terminalId) ||
            string.Equals(terminalId, "manual", StringComparison.Ordinal))
        {
            return OpenDefault(directory);
        }

        var profile = Discover().FirstOrDefault(item =>
            string.Equals(item.Id, terminalId, StringComparison.Ordinal));
        return profile is not null && Open(profile, directory);
    }

    public bool Open(TerminalProfile? profile, string directory)
    {
        if (profile is null)
            return false;
        return Start(profile, directory, profile.DirectoryArguments, null);
    }

    /// <summary>Runs a command in the configured terminal (used by "run in terminal" actions).</summary>
    public bool OpenCommand(string directory, string command)
    {
        var profile = GetDefaultProfile();
        return profile is { SupportsCommands: true } &&
               Start(profile, directory, profile.CommandArguments, command);
    }

    /// <summary>Opens the folder as a Herdr workspace inside the configured terminal.</summary>
    public bool OpenInHerdr(string directory)
    {
        var herdr = FindHerdr();
        if (herdr is null)
            return false;

        var terminal = GetDefaultProfile();
        if (terminal is not { SupportsCommands: true })
        {
            terminal = Discover().FirstOrDefault(profile => profile.SupportsCommands);
        }

        if (terminal is null)
            return false;

        var label = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        if (string.IsNullOrWhiteSpace(label))
            label = directory;

        var script = BuildHerdrScript(herdr.Executable, directory, label);
        return Start(terminal, directory, terminal.CommandArguments, script);
    }

    private static string BuildHerdrScript(string executable, string directory, string label)
    {
        if (OperatingSystem.IsWindows())
        {
            var quotedExecutable = CommandTemplateService.QuoteWindows(executable);
            var quotedDirectory = CommandTemplateService.QuoteWindows(directory);
            var quotedLabel = CommandTemplateService.QuoteWindows(label);
            return $"{quotedExecutable} workspace create --cwd {quotedDirectory} --label {quotedLabel} --focus " +
                   $">nul 2>&1 & {quotedExecutable}";
        }

        var exe = CommandTemplateService.QuotePosix(executable);
        var dir = CommandTemplateService.QuotePosix(directory);
        var name = CommandTemplateService.QuotePosix(label);
        return $"{exe} workspace create --cwd {dir} --label {name} --focus >/dev/null 2>&1 || true; exec {exe}";
    }

    private static TerminalProfile? BuildManualProfile(AppSettings settings)
    {
        var path = (settings.ManualTerminalPath ?? string.Empty).Trim();
        if (path.Length == 0)
            return null;

        if (path == "~")
        {
            path = AppPaths.Home;
        }
        else if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            path = Path.Combine(AppPaths.Home, path[2..]);
        }

        var name = (settings.ManualTerminalName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            try
            {
                name = Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                name = "Terminal";
            }
        }

        if (name.Length == 0)
            name = "Terminal";

        return new TerminalProfile(
            "manual",
            name,
            path,
            settings.ManualTerminalArguments ?? string.Empty,
            settings.ManualTerminalCommandArguments ?? string.Empty,
            IsManual: true);
    }

    private static bool Start(TerminalProfile profile, string directory,
        string? argumentsTemplate, string? command)
    {
        if (!Directory.Exists(directory))
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = profile.Executable,
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in CommandTemplateService.SplitArguments(
                         argumentsTemplate, directory, command))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex)
        {
            Log.Warn($"启动终端失败: {profile.Name}", ex);
            return false;
        }
    }
}
