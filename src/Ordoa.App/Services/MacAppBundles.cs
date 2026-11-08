using Ordoa.Core.Models;

namespace Ordoa.App.Services;

/// <summary>
/// macOS application bundles are directories on disk. When enabled, Ordoa
/// presents them as applications while keeping folder operations intact.
/// </summary>
public static class MacAppBundles
{
    public static bool Enabled => OperatingSystem.IsMacOS() &&
        (App.Services?.Settings.Current.MacAppBundles ?? true);

    public static bool IsApplication(FileEntry entry, bool isLocal = true)
    {
        if (!isLocal || !Enabled || !entry.IsDirectory ||
            !entry.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return LooksLikeApplicationBundle(entry.Path);
    }

    private static bool LooksLikeApplicationBundle(string path)
    {
        try
        {
            // Regular macOS application bundle.
            if (File.Exists(Path.Combine(path, "Contents", "Info.plist")))
                return true;

            // iOS apps installed on Apple Silicon wrap the real bundle in a
            // Wrapper directory; their Info.plist is at the bundle root.
            var wrapper = Path.Combine(path, "Wrapper");
            if (Directory.Exists(wrapper))
            {
                foreach (var bundle in Directory.EnumerateDirectories(wrapper))
                {
                    if (bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(Path.Combine(bundle, "Info.plist")))
                    {
                        return true;
                    }
                }

                // WrappedBundle is a symlink to the nested iOS app.
                var wrapped = Path.Combine(path, "WrappedBundle");
                if (Directory.Exists(wrapped) && File.Exists(Path.Combine(wrapped, "Info.plist")))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
