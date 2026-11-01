using System.Runtime.InteropServices;

namespace Ordoa.Core.Util;

public static class AppPaths
{
    public static bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsMacOS { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ConfigHome
    {
        get
        {
            if (IsWindows)
                return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (IsMacOS)
                return Path.Combine(Home, "Library", "Application Support");
            return Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Home, ".config");
        }
    }

    private static string DataHome
    {
        get
        {
            if (IsWindows)
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (IsMacOS)
                return Path.Combine(Home, "Library", "Application Support");
            return Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Home, ".local", "share");
        }
    }

    private static string CacheHome
    {
        get
        {
            if (IsWindows)
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cache");
            if (IsMacOS)
                return Path.Combine(Home, "Library", "Caches");
            return Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Home, ".cache");
        }
    }

    private static string StateHome
    {
        get
        {
            if (IsWindows)
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (IsMacOS)
                return Path.Combine(Home, "Library", "Logs");
            return Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Home, ".local", "state");
        }
    }

    private static string AppFolderName => IsWindows || IsMacOS ? "Ordoa" : "ordoa";

    public static string ConfigDirectory => Path.Combine(ConfigHome, AppFolderName);
    public static string DataDirectory => Path.Combine(DataHome, AppFolderName);
    public static string CacheDirectory => Path.Combine(CacheHome, AppFolderName);
    public static string StateDirectory => Path.Combine(StateHome, AppFolderName);

    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");
    public static string ConnectionsFile => Path.Combine(ConfigDirectory, "connections.json");
    public static string SessionFile => Path.Combine(ConfigDirectory, "session.json");
    public static string RecentFile => Path.Combine(ConfigDirectory, "recent.json");
    public static string CredentialsFile => Path.Combine(ConfigDirectory, "credentials.json");
    public static string LogFile => Path.Combine(StateDirectory, "ordoa.log");
    public static string ThumbnailCache => Path.Combine(CacheDirectory, "thumbnails");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    /// <summary>Trash root following the freedesktop.org trash specification.</summary>
    public static string TrashDirectory
        => Path.Combine(DataHome, "Trash");
}
