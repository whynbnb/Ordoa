namespace Ordoa.App.Services;

/// <summary>
/// Lightweight desktop-environment detection used by the window chrome.
/// Tiling compositors typically have no server-side title bar, so Ordoa
/// hides its own caption buttons there by default.
/// </summary>
public static class WindowEnvironment
{
    private static readonly HashSet<string> TilingDesktops = new(StringComparer.OrdinalIgnoreCase)
    {
        "niri",
        "sway",
        "hyprland",
        "river",
        "i3",
        "bspwm",
        "dwm",
        "awesome",
        "qtile",
        "xmonad",
        "herbstluftwm",
        "spectrwm",
        "leftwm",
        "miracle-wm",
    };

    public static bool IsTilingCompositor { get; } = DetectTilingCompositor();

    private static bool DetectTilingCompositor()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NIRI_SOCKET")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWAYSOCK")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")))
        {
            return true;
        }

        foreach (var variable in new[]
                 {
                     "XDG_CURRENT_DESKTOP",
                     "XDG_SESSION_DESKTOP",
                     "DESKTOP_SESSION",
                     "GDMSESSION",
                 })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            foreach (var part in value.Split([':', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TilingDesktops.Contains(part))
                    return true;
            }
        }

        return false;
    }
}
