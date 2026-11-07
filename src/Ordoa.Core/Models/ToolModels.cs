namespace Ordoa.Core.Models;

/// <summary>How Ordoa finds terminal emulators.</summary>
public enum TerminalDetectionMode
{
    Auto,
    Manual,
}

/// <summary>Persisted per-terminal choices. The terminal itself is discovered at runtime.</summary>
public sealed class TerminalPreference
{
    public string Id { get; set; } = string.Empty;
    public bool ShowInContextMenu { get; set; }
    public bool IsDefault { get; set; }

    public TerminalPreference Clone() => new()
    {
        Id = Id,
        ShowInContextMenu = ShowInContextMenu,
        IsDefault = IsDefault,
    };
}

/// <summary>A terminal found on this machine together with its launch arguments.</summary>
public sealed record TerminalProfile(
    string Id,
    string Name,
    string Executable,
    string DirectoryArguments,
    string CommandArguments,
    bool IsHerdr = false,
    bool IsManual = false)
{
    public bool SupportsCommands => !string.IsNullOrWhiteSpace(CommandArguments);
}

/// <summary>A named search expression kept in the search flyout.</summary>
public sealed class SavedSearch
{
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;

    public SavedSearch Clone() => new() { Name = Name, Query = Query };
}

/// <summary>A user-defined quick action executed from the context menu or command palette.</summary>
public sealed class CustomCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = "%d";
    public bool ShowInContextMenu { get; set; } = true;
    public bool RunInTerminal { get; set; }
    public bool RequiresSelection { get; set; }
    /// <summary>Ask for confirmation before executing.</summary>
    public bool ConfirmBeforeRun { get; set; }
    /// <summary>Write selected paths to the command's standard input.</summary>
    public bool PipeSelectionToStdin { get; set; }
    /// <summary>Extra environment variables as KEY=VALUE lines.</summary>
    public string EnvironmentVariables { get; set; } = string.Empty;
    /// <summary>Show this action in the toolbar's quick actions menu.</summary>
    public bool PinnedToToolbar { get; set; }

    public CustomCommand Clone() => new()
    {
        Id = Id,
        Name = Name,
        Command = Command,
        WorkingDirectory = WorkingDirectory,
        ShowInContextMenu = ShowInContextMenu,
        RunInTerminal = RunInTerminal,
        RequiresSelection = RequiresSelection,
        ConfirmBeforeRun = ConfirmBeforeRun,
        PipeSelectionToStdin = PipeSelectionToStdin,
        EnvironmentVariables = EnvironmentVariables,
        PinnedToToolbar = PinnedToToolbar,
    };
}
