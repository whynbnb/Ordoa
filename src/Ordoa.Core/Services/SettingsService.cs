using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum AccentKind
{
    Jade,
    Blue,
    Amber,
    Rose,
    Violet,
}

public enum WindowButtonMode
{
    Auto,
    Show,
    Hide,
}

public enum FolderOpenMode
{
    CurrentWindow,
    NewWindow,
}

public enum UndoBehavior
{
    System,
    Full,
    StructureOnly,
    Disabled,
}

public enum PreviewPanelPlacement
{
    Right,
    Bottom,
}

public enum CopyVerificationMode
{
    None,
    Size,
    Sha256,
}

/// <summary>Per-folder view and ordering overrides.</summary>
public sealed class FolderViewState
{
    public SortColumn SortColumn { get; set; } = SortColumn.Name;
    public bool SortDescending { get; set; }
    public bool FoldersFirst { get; set; } = true;
    public ViewKind View { get; set; } = ViewKind.List;

    public FolderViewState Clone() => new()
    {
        SortColumn = SortColumn,
        SortDescending = SortDescending,
        FoldersFirst = FoldersFirst,
        View = View,
    };
}

public sealed class AppSettings
{
    public UiLanguage Language { get; set; } = UiLanguage.System;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public AccentKind Accent { get; set; } = AccentKind.Jade;
    public bool CompactMode { get; set; }
    /// <summary>Collapse explanatory hints into an info icon with a tooltip.</summary>
    public bool CollapseHints { get; set; } = true;
    /// <summary>Treat macOS .app directories as applications (macOS only).</summary>
    public bool MacAppBundles { get; set; } = OperatingSystem.IsMacOS();
    public bool ShowHidden { get; set; }
    public bool FoldersFirst { get; set; } = true;
    public SortColumn SortColumn { get; set; } = SortColumn.Name;
    public bool SortDescending { get; set; }
    public ViewKind View { get; set; } = ViewKind.List;
    public bool ConfirmPermanentDelete { get; set; } = true;
    public bool RestoreSession { get; set; } = true;
    public bool ShowSidebar { get; set; } = true;
    public bool ShowVolumes { get; set; } = true;
    public bool ShowHiddenVolumes { get; set; }
    /// <summary>User-added sidebar locations (absolute paths).</summary>
    public List<string> CustomSidebarPaths { get; set; } = [];
    /// <summary>Known-folder keys hidden from the sidebar's Places section.</summary>
    public List<string> HiddenPlaces { get; set; } = [];
    /// <summary>Volume mount points hidden from the sidebar's Disks section.</summary>
    public List<string> HiddenVolumes { get; set; } = [];
    /// <summary>Connection ids hidden from the sidebar's Network section.</summary>
    public List<string> HiddenConnections { get; set; } = [];
    public double SidebarWidth { get; set; } = 232;
    public bool ShowThumbnails { get; set; } = true;
    public bool ShowTransferPanel { get; set; } = true;
    /// <summary>Visible columns in the list view.</summary>
    public bool ShowSizeColumn { get; set; } = true;
    public bool ShowModifiedColumn { get; set; } = true;
    public bool ShowTypeColumn { get; set; } = true;
    public bool ShowPermissionColumn { get; set; } = true;
    public bool ShowOwnerColumn { get; set; } = true;
    /// <summary>Remembered sort/view settings keyed by filesystem id and path.</summary>
    public Dictionary<string, FolderViewState> FolderViews { get; set; } = [];
    /// <summary>Keep a preview panel open for the current selection.</summary>
    public bool ShowPreviewPanel { get; set; }
    /// <summary>Where the persistent preview panel is docked.</summary>
    public PreviewPanelPlacement PreviewPanelPlacement { get; set; } = PreviewPanelPlacement.Right;
    public int SearchDepth { get; set; } = 12;
    public int MaxSearchResults { get; set; } = 3000;
    /// <summary>Recent successful search expressions, newest first.</summary>
    public List<string> SearchHistory { get; set; } = [];
    /// <summary>Named search expressions shown in the search flyout.</summary>
    public List<SavedSearch> SavedSearches { get; set; } = [];
    /// <summary>Recently executed command palette entries, newest first.</summary>
    public List<string> PaletteRecent { get; set; } = [];
    /// <summary>Execution counts for command palette entries.</summary>
    public Dictionary<string, int> PaletteUsage { get; set; } = [];
    /// <summary>How copied files are verified after the transfer completes.</summary>
    public CopyVerificationMode CopyVerification { get; set; } = CopyVerificationMode.Size;
    /// <summary>Show a conflict summary before a batch copy or move starts.</summary>
    public bool ShowTransferPreview { get; set; } = true;
    /// <summary>Upload local edits back to remote files opened from a remote pane.</summary>
    public bool UploadRemoteEdits { get; set; } = true;
    /// <summary>Number of transfer jobs allowed to run at once.</summary>
    public int TransferConcurrency { get; set; } = 1;
    /// <summary>Global transfer speed limit in MB/s. Zero means unlimited.</summary>
    public int TransferSpeedLimitMb { get; set; }
    /// <summary>7z compression level (0-9).</summary>
    public int ArchiveCompressionLevel { get; set; } = 5;
    /// <summary>Split 7z archives into volumes of this size in MB. Zero disables splitting.</summary>
    public int ArchiveSplitMb { get; set; }
    /// <summary>Optional path to the ffmpeg executable used for media previews.</summary>
    public string FfmpegPath { get; set; } = string.Empty;
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 760;
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Keep the platform-drawn title bar / non-client area. The default is the
    /// immersive, app-drawn title bar that blends into the tab strip.
    /// </summary>
    public bool UseSystemWindowFrame { get; set; }

    /// <summary>
    /// Controls the app-drawn minimize/maximize/close buttons. Auto hides them
    /// on tiling compositors such as niri, Sway and Hyprland.
    /// </summary>
    public WindowButtonMode WindowButtonMode { get; set; } = WindowButtonMode.Auto;

    /// <summary>Reuse the running process when Ordoa is launched again.</summary>
    public bool SingleInstance { get; set; } = true;

    /// <summary>Allow more than one Ordoa window in the same process.</summary>
    public bool AllowMultipleWindows { get; set; } = true;

    /// <summary>Where folders opened from the shell or other apps should go.</summary>
    public FolderOpenMode FolderOpenMode { get; set; } = FolderOpenMode.CurrentWindow;

    /// <summary>Remembered application choice per lowercase extension (empty key for no extension).</summary>
    public Dictionary<string, string> OpenWithDefaults { get; set; } = [];

    /// <summary>Key bindings that differ from the defaults, keyed by action id. Empty disables.</summary>
    public Dictionary<string, string> ShortcutOverrides { get; set; } = [];

    /// <summary>Session undo scope. System follows the platform file manager behavior.</summary>
    public UndoBehavior UndoBehavior { get; set; } = UndoBehavior.System;

    /// <summary>Enable read-only git status probing and badges.</summary>
    public bool EnableGitIntegration { get; set; } = true;
    public bool ShowGitStatusBar { get; set; } = true;
    public bool ShowGitFileBadges { get; set; } = true;
    public bool ShowGitCleanCheck { get; set; } = true;
    public bool ShowGitRepositoryBadges { get; set; } = true;
    public bool ShowGitSubmoduleBadges { get; set; } = true;
    public bool DimGitIgnored { get; set; } = true;

    /// <summary>Whether terminals are discovered from PATH or supplied manually.</summary>
    public TerminalDetectionMode TerminalMode { get; set; } = TerminalDetectionMode.Auto;

    public string ManualTerminalName { get; set; } = string.Empty;
    public string ManualTerminalPath { get; set; } = string.Empty;
    /// <summary>Arguments used when opening a folder. Supports %d.</summary>
    public string ManualTerminalArguments { get; set; } = string.Empty;
    /// <summary>Arguments used when running a command. Supports %d and %c.</summary>
    public string ManualTerminalCommandArguments { get; set; } = string.Empty;

    /// <summary>Default terminal and context-menu visibility, keyed by discovered terminal id.</summary>
    public List<TerminalPreference> TerminalPreferences { get; set; } = [];

    /// <summary>User-defined quick actions run from the context menu or command palette.</summary>
    public List<CustomCommand> CustomCommands { get; set; } = [];

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.CustomSidebarPaths = [.. CustomSidebarPaths];
        clone.HiddenPlaces = [.. HiddenPlaces];
        clone.HiddenVolumes = [.. HiddenVolumes];
        clone.HiddenConnections = [.. HiddenConnections];
        clone.OpenWithDefaults = new Dictionary<string, string>(OpenWithDefaults, StringComparer.Ordinal);
        clone.ShortcutOverrides = new Dictionary<string, string>(ShortcutOverrides, StringComparer.Ordinal);
        clone.TerminalPreferences = (TerminalPreferences ?? [])
            .Where(preference => preference is not null)
            .Select(preference => preference.Clone())
            .ToList();
        clone.CustomCommands = (CustomCommands ?? [])
            .Where(command => command is not null)
            .Select(command => command.Clone())
            .ToList();
        clone.SearchHistory = [.. SearchHistory];
        clone.PaletteRecent = [.. PaletteRecent];
        clone.PaletteUsage = new Dictionary<string, int>(PaletteUsage, StringComparer.Ordinal);
        clone.FolderViews = (FolderViews ?? [])
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        clone.SavedSearches = (SavedSearches ?? [])
            .Where(search => search is not null)
            .Select(search => search.Clone())
            .ToList();
        return clone;
    }
}

public sealed class SettingsService
{
    private readonly object _sync = new();

    public SettingsService()
    {
        Current = JsonFile.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
        Sanitize();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler? Changed;

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                Sanitize();
                JsonFile.Save(AppPaths.SettingsFile, Current);
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Error("保存设置失败", ex);
            }
        }
    }

    public void Replace(AppSettings settings)
    {
        Current = settings;
        Save();
    }

    public void Reset()
    {
        Current = new AppSettings();
        Save();
    }

    private void Sanitize()
    {
        if (Current.SidebarWidth is < 200 or > 480)
            Current.SidebarWidth = 232;
        if (!Enum.IsDefined(Current.Language))
            Current.Language = UiLanguage.System;
        if (!Enum.IsDefined(Current.WindowButtonMode))
            Current.WindowButtonMode = WindowButtonMode.Auto;
        if (!Enum.IsDefined(Current.FolderOpenMode))
            Current.FolderOpenMode = FolderOpenMode.CurrentWindow;
        if (!Enum.IsDefined(Current.UndoBehavior))
            Current.UndoBehavior = UndoBehavior.System;
        if (!Enum.IsDefined(Current.PreviewPanelPlacement))
            Current.PreviewPanelPlacement = PreviewPanelPlacement.Right;
        if (!Enum.IsDefined(Current.TerminalMode))
            Current.TerminalMode = TerminalDetectionMode.Auto;
        Current.ManualTerminalName = (Current.ManualTerminalName ?? string.Empty).Trim();
        Current.ManualTerminalPath = (Current.ManualTerminalPath ?? string.Empty).Trim();
        Current.ManualTerminalArguments = (Current.ManualTerminalArguments ?? string.Empty).Trim();
        Current.ManualTerminalCommandArguments = (Current.ManualTerminalCommandArguments ?? string.Empty).Trim();
        Current.TerminalPreferences = (Current.TerminalPreferences ?? [])
            .Where(preference => preference is not null && !string.IsNullOrWhiteSpace(preference.Id))
            .GroupBy(preference => preference.Id.Trim(), StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
        var defaultTerminal = Current.TerminalPreferences.LastOrDefault(preference => preference.IsDefault);
        foreach (var preference in Current.TerminalPreferences)
            preference.IsDefault = ReferenceEquals(preference, defaultTerminal);

        var seenCommandIds = new HashSet<string>(StringComparer.Ordinal);
        Current.CustomCommands = (Current.CustomCommands ?? [])
            .Where(command => command is not null && !string.IsNullOrWhiteSpace(command.Command))
            .Select(command =>
            {
                command.Id = string.IsNullOrWhiteSpace(command.Id)
                    ? Guid.NewGuid().ToString("N")
                    : command.Id.Trim();
                command.Name = (command.Name ?? string.Empty).Trim();
                command.Command = (command.Command ?? string.Empty).Trim();
                if (command.Name.Length == 0)
                {
                    var end = command.Command.IndexOfAny([' ', '\t', '\r', '\n']);
                    command.Name = end > 0 ? command.Command[..end] : command.Command;
                }
                command.WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory)
                    ? "%d"
                    : command.WorkingDirectory.Trim();
                return command;
            })
            .Where(command => seenCommandIds.Add(command.Id))
            .ToList();
        Current.OpenWithDefaults = (Current.OpenWithDefaults ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(pair => pair.Key.Trim().TrimStart('.').ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        Current.ShortcutOverrides = (Current.ShortcutOverrides ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .GroupBy(pair => pair.Key.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (group.Last().Value ?? string.Empty).Trim(),
                StringComparer.Ordinal);
        if (Current.WindowWidth < 720)
            Current.WindowWidth = 1200;
        if (Current.WindowHeight < 480)
            Current.WindowHeight = 760;
        Current.HiddenPlaces = (Current.HiddenPlaces ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Current.HiddenConnections = (Current.HiddenConnections ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Current.CustomSidebarPaths = (Current.CustomSidebarPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Current.SearchDepth = Math.Clamp(Current.SearchDepth, 1, 64);
        Current.MaxSearchResults = Math.Clamp(Current.MaxSearchResults, 100, 20000);
        Current.FolderViews = (Current.FolderViews ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(500)
            .ToDictionary(group => group.Key, group =>
            {
                var state = group.Last().Value;
                if (!Enum.IsDefined(state.SortColumn))
                    state.SortColumn = SortColumn.Name;
                if (!Enum.IsDefined(state.View))
                    state.View = ViewKind.List;
                return state;
            }, StringComparer.Ordinal);
        Current.SearchHistory = (Current.SearchHistory ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();
        if (!Enum.IsDefined(Current.CopyVerification))
            Current.CopyVerification = CopyVerificationMode.Size;
        Current.TransferConcurrency = Math.Clamp(Current.TransferConcurrency, 1, 4);
        Current.TransferSpeedLimitMb = Math.Clamp(Current.TransferSpeedLimitMb, 0, 100_000);
        Current.ArchiveCompressionLevel = Math.Clamp(Current.ArchiveCompressionLevel, 0, 9);
        Current.ArchiveSplitMb = Math.Clamp(Current.ArchiveSplitMb, 0, 100_000);
        Current.PaletteRecent = (Current.PaletteRecent ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToList();
        Current.PaletteUsage = (Current.PaletteUsage ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .Take(300)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Current.SavedSearches = (Current.SavedSearches ?? [])
            .Where(search => search is not null && !string.IsNullOrWhiteSpace(search.Query))
            .Select(search =>
            {
                search.Query = search.Query.Trim();
                search.Name = string.IsNullOrWhiteSpace(search.Name) ? search.Query : search.Name.Trim();
                return search;
            })
            .Take(50)
            .ToList();
        Current.FfmpegPath = (Current.FfmpegPath ?? string.Empty).Trim();
    }
}
