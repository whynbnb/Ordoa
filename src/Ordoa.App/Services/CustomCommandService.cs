using System.Diagnostics;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

/// <summary>Runs user-defined quick actions with %f/%d-style placeholders.</summary>
public sealed class CustomCommandService
{
    private readonly AppServices _services;

    public CustomCommandService(AppServices services)
    {
        _services = services;
    }

    public IReadOnlyList<CustomCommand> GetAll()
        => _services.Settings.Current.CustomCommands;

    public IReadOnlyList<CustomCommand> GetContextMenuCommands()
        => GetAll()
            .Where(command => command.ShowInContextMenu && !string.IsNullOrWhiteSpace(command.Command))
            .ToList();

    public IReadOnlyList<CustomCommand> GetToolbarCommands()
        => GetAll()
            .Where(command => command.PinnedToToolbar && !string.IsNullOrWhiteSpace(command.Command))
            .ToList();

    public CustomCommand? Find(string id)
        => GetAll().FirstOrDefault(command =>
            string.Equals(command.Id, id, StringComparison.Ordinal));

    public bool CanRun(CustomCommand command, bool hasSelection)
        => !command.RequiresSelection || hasSelection;

    public async Task<bool> RunAsync(CustomCommand command, string workingDirectory,
        IReadOnlyList<string> selectedPaths)
    {
        if (string.IsNullOrWhiteSpace(command.Command))
            return false;

        if (command.ConfirmBeforeRun)
        {
            var confirmed = await _services.Dialogs.ConfirmAsync(
                L10n.T("运行自定义命令？"), $"{command.Name}\n{command.Command}", L10n.T("运行"))
                .ConfigureAwait(true);
            if (!confirmed)
                return false;
        }

        var directory = ResolveWorkingDirectory(command.WorkingDirectory, workingDirectory, selectedPaths);
        var commandLine = CommandTemplateService.Expand(
            command.Command, workingDirectory, selectedPaths, quote: true);
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        if (command.RunInTerminal)
            return _services.Terminals.OpenCommand(directory, commandLine);

        var environment = ParseEnvironment(command.EnvironmentVariables);
        return StartShell(commandLine, directory, environment,
            command.PipeSelectionToStdin ? selectedPaths : null);
    }

    private static IReadOnlyDictionary<string, string> ParseEnvironment(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = raw.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = raw[..separator].Trim();
            if (key.Length == 0)
                continue;
            result[key] = raw[(separator + 1)..];
        }
        return result;
    }

    private static string ResolveWorkingDirectory(string? template, string workingDirectory,
        IReadOnlyList<string> selectedPaths)
    {
        var expanded = CommandTemplateService.Expand(template, workingDirectory, selectedPaths, quote: false).Trim();
        if (expanded.Length == 0)
            return workingDirectory;

        try
        {
            var candidate = expanded;
            if (!Path.IsPathRooted(candidate))
                candidate = Path.Combine(workingDirectory, candidate);

            if (Directory.Exists(candidate))
                return candidate;
            if (File.Exists(candidate))
                return Path.GetDirectoryName(candidate) ?? workingDirectory;
        }
        catch
        {
            // Fall back to the current pane directory for malformed paths.
        }

        return workingDirectory;
    }

    private static bool StartShell(string commandLine, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, IReadOnlyList<string>? stdinPaths)
    {
        try
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
                : new ProcessStartInfo
                {
                    FileName = File.Exists("/bin/sh") ? "/bin/sh" : "sh",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

            if (OperatingSystem.IsWindows())
                startInfo.ArgumentList.Add("/c");
            else
                startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(commandLine);
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
            if (stdinPaths is not null)
                startInfo.RedirectStandardInput = true;

            using var process = Process.Start(startInfo);
            if (process is null)
                return false;

            if (stdinPaths is not null)
            {
                try
                {
                    process.StandardInput.Write(string.Join(Environment.NewLine, stdinPaths));
                    process.StandardInput.Close();
                }
                catch
                {
                    // The command may exit before reading standard input.
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("运行自定义命令失败", ex);
            return false;
        }
    }
}
