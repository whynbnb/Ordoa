using Ordoa.Core.Localization;
using System.Text;

namespace Ordoa.Core.Util;

public sealed record KnownFolder(string Key, string Label, string Path);

public static class KnownFolders
{
    public static string Home => AppPaths.Home;

    public static string Desktop => Get("XDG_DESKTOP_DIR", "Desktop", "桌面");
    public static string Documents => Get("XDG_DOCUMENTS_DIR", "Documents", "文档");
    public static string Downloads => Get("XDG_DOWNLOAD_DIR", "Downloads", "下载");
    public static string Pictures => Get("XDG_PICTURES_DIR", "Pictures", "图片");
    public static string Music => Get("XDG_MUSIC_DIR", "Music", "音乐");
    public static string Videos => Get("XDG_VIDEOS_DIR", "Videos", "视频");
    public static string Public => Get("XDG_PUBLICSHARE_DIR", "Public", "公共");
    public static string Templates => Get("XDG_TEMPLATES_DIR", "Templates", "模板");

    public static IReadOnlyList<KnownFolder> GetPlaces()
    {
        var all = new List<KnownFolder>
        {
            new("home", L10n.T("主目录"), Home),
            new("desktop", L10n.T("桌面"), Desktop),
            new("documents", L10n.T("文档"), Documents),
            new("downloads", L10n.T("下载"), Downloads),
            new("pictures", L10n.T("图片"), Pictures),
            new("music", L10n.T("音乐"), Music),
            new("videos", L10n.T("视频"), Videos),
            new("public", L10n.T("公共"), Public),
            new("templates", L10n.T("模板"), Templates),
        };

        return all.Where(f => Directory.Exists(f.Path)).ToList();
    }

    /// <summary>Localized label for a well-known folder path, or null.</summary>
    public static string? GetDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }

        foreach (var folder in GetPlaces())
        {
            try
            {
                if (string.Equals(Path.GetFullPath(folder.Path), full, StringComparison.Ordinal))
                    return folder.Label;
            }
            catch
            {
                // Ignore malformed paths.
            }
        }
        return null;
    }

    private static string Get(string xdgKey, string fallbackName, string chineseName)
    {
        var value = ReadUserDirs().GetValueOrDefault(xdgKey);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        var home = AppPaths.Home;
        var standard = Path.Combine(home, fallbackName);
        if (Directory.Exists(standard))
            return standard;

        var localized = Path.Combine(home, chineseName);
        return Directory.Exists(localized) ? localized : standard;
    }

    private static Dictionary<string, string> ReadUserDirs()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome))
                configHome = Path.Combine(AppPaths.Home, ".config");
            var file = Path.Combine(configHome, "user-dirs.dirs");
            if (!File.Exists(file))
                return result;

            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;
                var equals = trimmed.IndexOf('=');
                if (equals <= 0)
                    continue;
                var key = trimmed[..equals].Trim();
                var value = trimmed[(equals + 1)..].Trim().Trim('"');
                if (value.StartsWith("$HOME", StringComparison.Ordinal))
                    value = AppPaths.Home + value[5..];
                if (value.Length > 0)
                    result[key] = value.Replace("\\", "/").TrimEnd('/');
            }
        }
        catch
        {
            // Ignore malformed user-dirs files.
        }
        return result;
    }

    /// <summary>Location shown when the app starts and when "Home" is activated.</summary>
    public static string DefaultStartupPath
    {
        get
        {
            var home = Home;
            return Directory.Exists(home) ? home : "/";
        }
    }

    public static string DisplayNameForPath(string path)
    {
        path = path.TrimEnd('/');
        if (path.Length == 0)
            return "/";
        var known = GetPlaces().FirstOrDefault(p => string.Equals(p.Path.TrimEnd('/'), path, StringComparison.Ordinal));
        return known?.Label ?? Path.GetFileName(path);
    }

    public static string ShortenForDisplay(string path)
    {
        var home = AppPaths.Home.TrimEnd('/');
        if (path.StartsWith(home + "/", StringComparison.Ordinal))
            return "~" + path[home.Length..];
        if (path == home)
            return "~";
        return path;
    }
}
