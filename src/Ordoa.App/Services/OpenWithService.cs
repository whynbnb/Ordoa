using System.Diagnostics;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

/// <summary>
/// Discovers freedesktop.org desktop entries and launches files with them.
/// Only Linux has a meaningful application registry; other platforms fall
/// back to the system default handler.
/// </summary>
public sealed class OpenWithService
{
    private readonly AppServices _services;
    private readonly object _sync = new();
    private List<DesktopApplication>? _applications;

    public OpenWithService(AppServices services)
    {
        _services = services;
        L10n.LanguageChanged += InvalidateCache;
    }

    private void InvalidateCache()
    {
        lock (_sync)
            _applications = null;
    }

    public bool IsSupported => AppPaths.IsLinux;

    public IReadOnlyList<DesktopApplication> GetApplications(FileEntry entry)
    {
        if (!IsSupported)
            return [];

        var applications = GetApplications();
        var mime = (string.IsNullOrWhiteSpace(entry.MimeType)
            ? MimeTypes.FromExtension(entry.Extension)
            : entry.MimeType).ToLowerInvariant();

        var matching = applications
            .Where(app => app.MimeTypes.Contains(mime))
            .ToList();

        return matching.Count > 0
            ? matching
            : applications.Where(app => !app.NoDisplay).ToList();
    }

    public DesktopApplication? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IsSupported)
            return null;

        return GetApplications().FirstOrDefault(app =>
            string.Equals(app.Id, id, StringComparison.Ordinal));
    }

    public string? GetDefault(string extension)
    {
        if (!IsSupported)
            return null;

        var key = NormalizeExtension(extension);
        return _services.Settings.Current.OpenWithDefaults.GetValueOrDefault(key);
    }

    public void Remember(string extension, string desktopId)
    {
        var key = NormalizeExtension(extension);
        _services.Settings.Current.OpenWithDefaults[key] = desktopId;
        _services.Settings.Save();
    }

    public void ClearDefault(string extension)
    {
        var key = NormalizeExtension(extension);
        if (_services.Settings.Current.OpenWithDefaults.Remove(key))
            _services.Settings.Save();
    }

    public void ClearDefaults()
    {
        if (_services.Settings.Current.OpenWithDefaults.Count == 0)
            return;

        _services.Settings.Current.OpenWithDefaults.Clear();
        _services.Settings.Save();
    }

    /// <summary>Uses a remembered application when one exists; false means the caller should use the system default.</summary>
    public bool TryOpenDefault(string path)
    {
        if (!IsSupported)
            return false;

        var extension = Path.GetExtension(path);
        var desktopId = GetDefault(extension);
        if (desktopId is null)
            return false;

        var app = Find(desktopId);
        if (app is null)
        {
            ClearDefault(extension);
            return false;
        }

        if (!Open(path, app))
        {
            ClearDefault(extension);
            return false;
        }

        return true;
    }

    public bool Open(string path, DesktopApplication application)
    {
        try
        {
            var arguments = DesktopEntryService.BuildArguments(application.Exec, path);
            if (arguments.Count == 0)
                return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = arguments[0],
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? AppPaths.Home,
            };

            for (var i = 1; i < arguments.Count; i++)
                startInfo.ArgumentList.Add(arguments[i]);

            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    private IReadOnlyList<DesktopApplication> GetApplications()
    {
        lock (_sync)
        {
            return _applications ??= DiscoverApplications();
        }
    }

    private static List<DesktopApplication> DiscoverApplications()
    {
        var directories = new List<string>();
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        directories.Add(Path.Combine(
            string.IsNullOrWhiteSpace(dataHome) ? Path.Combine(AppPaths.Home, ".local", "share") : dataHome,
            "applications"));

        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        foreach (var directory in (string.IsNullOrWhiteSpace(dataDirs) ? "/usr/local/share:/usr/share" : dataDirs)
                     .Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            directories.Add(Path.Combine(directory, "applications"));
        }

        // Common package-manager exports that are not always listed in XDG_DATA_DIRS.
        directories.Add(Path.Combine(AppPaths.Home, ".local", "share", "flatpak", "exports", "share", "applications"));
        directories.Add("/var/lib/flatpak/exports/share/applications");
        directories.Add("/var/lib/snapd/desktop/applications");
        directories = directories.Distinct(StringComparer.Ordinal).ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var applications = new List<DesktopApplication>();

        foreach (var directory in directories)
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(directory))
                    continue;
                files = Directory.EnumerateFiles(directory, "*.desktop", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                });
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var id = Path.GetFileName(file);
                if (seen.Contains(id))
                    continue;

                var app = DesktopEntryService.Parse(file, id);
                if (app is null || app.Terminal || !DesktopEntryService.IsExecutableAvailable(app.TryExec))
                    continue;

                seen.Add(id);
                applications.Add(app);
            }
        }

        applications.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return applications;
    }

    private static string NormalizeExtension(string extension)
        => extension.Trim().TrimStart('.').ToLowerInvariant();
}
