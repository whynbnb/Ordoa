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

public sealed class AppSettings
{
    public UiLanguage Language { get; set; } = UiLanguage.System;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public AccentKind Accent { get; set; } = AccentKind.Jade;
    public bool CompactMode { get; set; }
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
    public double SidebarWidth { get; set; } = 232;
    public bool ShowThumbnails { get; set; } = true;
    public bool ShowTransferPanel { get; set; } = true;
    public int SearchDepth { get; set; } = 12;
    public int MaxSearchResults { get; set; } = 3000;
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

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.CustomSidebarPaths = [.. CustomSidebarPaths];
        clone.HiddenPlaces = [.. HiddenPlaces];
        clone.OpenWithDefaults = new Dictionary<string, string>(OpenWithDefaults, StringComparer.Ordinal);
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
        Current.OpenWithDefaults = (Current.OpenWithDefaults ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(pair => pair.Key.Trim().TrimStart('.').ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        if (Current.WindowWidth < 720)
            Current.WindowWidth = 1200;
        if (Current.WindowHeight < 480)
            Current.WindowHeight = 760;
        Current.HiddenPlaces = (Current.HiddenPlaces ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Current.CustomSidebarPaths = (Current.CustomSidebarPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Current.SearchDepth = Math.Clamp(Current.SearchDepth, 1, 64);
        Current.MaxSearchResults = Math.Clamp(Current.MaxSearchResults, 100, 20000);
        Current.FfmpegPath = (Current.FfmpegPath ?? string.Empty).Trim();
    }
}
