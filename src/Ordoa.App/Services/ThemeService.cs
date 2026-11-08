using Avalonia.Media;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Styling;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

public static class ThemeService
{
    private sealed record DarkPalette(
        string Window,
        string Surface,
        string SurfaceAlt,
        string Sidebar,
        string Border,
        string BorderStrong,
        string TextPrimary,
        string TextSecondary,
        string TextTertiary,
        string Hover,
        string Pressed,
        string Toolbar,
        string Status,
        string Menu,
        string Input,
        string Scroll,
        string ScrollHover);

    private static readonly Dictionary<DarkBackground, DarkPalette> DarkPalettes = new()
    {
        [DarkBackground.Graphite] = new("#141816", "#1B201D", "#222824", "#171C19", "#2A312D",
            "#3A433E", "#E9EEEA", "#A3ADA7", "#748078", "#252C28", "#2E3631", "#181D1A",
            "#151A17", "#202622", "#1E2420", "#3A443E", "#4C5951"),
        [DarkBackground.Midnight] = new("#0B1220", "#111A2B", "#172238", "#0D1524", "#22304A",
            "#31425F", "#E6EDF7", "#9BA9C0", "#6F7F99", "#1B2940", "#23324A", "#0E1727",
            "#0C1422", "#141F33", "#131D2E", "#334663", "#496080"),
        [DarkBackground.Slate] = new("#151719", "#1C1F23", "#23272C", "#171A1E", "#2B3036",
            "#3A414A", "#ECEEF1", "#A5ABB4", "#737B85", "#252A30", "#2E343B", "#181B1F",
            "#14161A", "#20242A", "#1E2227", "#3B424B", "#4E5660"),
        [DarkBackground.Ocean] = new("#0C1719", "#112124", "#182B2E", "#0E1B1D", "#214044",
            "#2E565A", "#E3F2F1", "#9DB8B5", "#6B8986", "#193034", "#214044", "#0F1C1E",
            "#0B1517", "#152A2D", "#132528", "#2E4F53", "#416B70"),
        [DarkBackground.Forest] = new("#101713", "#17211A", "#1E2C23", "#121B15", "#2B4032",
            "#3A5544", "#E7F1E9", "#A7BBA9", "#748A77", "#1D2D22", "#273B2D", "#131D16",
            "#0E1611", "#1A2A1F", "#18251C", "#34523E", "#48725A"),
        [DarkBackground.Warm] = new("#1A1512", "#221C18", "#2B231E", "#1C1613", "#3B3029",
            "#4E4037", "#F4ECE5", "#C0AEA1", "#8A786C", "#2A211C", "#352A24", "#1D1714",
            "#17120F", "#261E1A", "#241D19", "#45372F", "#5A493E"),
        [DarkBackground.Black] = new("#000000", "#0A0A0A", "#111111", "#050505", "#1E1E1E",
            "#2C2C2C", "#F2F2F2", "#A6A6A6", "#737373", "#141414", "#1C1C1C", "#050505",
            "#030303", "#0E0E0E", "#0D0D0D", "#2A2A2A", "#3B3B3B"),
    };

    private static readonly string[] DarkPaletteKeys =
    [
        "WindowBackgroundBrush", "SurfaceBrush", "SurfaceAltBrush", "SidebarBrush",
        "BorderBrush", "BorderStrongBrush", "TextPrimaryBrush", "TextSecondaryBrush",
        "TextTertiaryBrush", "HoverBrush", "PressedBrush", "ToolbarBrush", "StatusBrush",
        "MenuBackgroundBrush", "InputBackgroundBrush", "ScrollThumbBrush", "ScrollThumbHoverBrush",
    ];

    private static readonly Dictionary<AccentKind, (string Base, string Text)> Accents = new()
    {
        [AccentKind.Jade] = ("#17937F", "#FFFFFF"),
        [AccentKind.Blue] = ("#3B7EDB", "#FFFFFF"),
        [AccentKind.Amber] = ("#C9862B", "#241A05"),
        [AccentKind.Rose] = ("#C2688A", "#FFFFFF"),
        [AccentKind.Violet] = ("#7C63D8", "#FFFFFF"),
    };

    private static DarkBackground _darkBackground = DarkBackground.Graphite;
    private static AccentKind _accent = AccentKind.Jade;
    private static bool _highContrast;
    private static bool _themeSubscribed;

    private static readonly string[] HighContrastExtraKeys =
    [
        "ErrorBrush", "ErrorSoftBrush", "WarningBrush", "WarningSoftBrush", "SuccessBrush", "OverlayBrush",
    ];

    public static void Apply(AppSettings settings)
    {
        var app = Application.Current;
        if (app is null)
            return;

        _darkBackground = settings.DarkBackground;
        _accent = settings.Accent;
        _highContrast = settings.Theme == AppTheme.HighContrast;

        app.RequestedThemeVariant = settings.Theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.HighContrast => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        ApplyCurrentTheme(app);

        if (!_themeSubscribed)
        {
            _themeSubscribed = true;
            app.ActualThemeVariantChanged += (_, _) =>
            {
                if (Application.Current is { } current)
                    ApplyCurrentTheme(current);
            };
        }
    }

    private static void ApplyCurrentTheme(Application app)
    {
        var isDark = app.ActualThemeVariant == ThemeVariant.Dark;
        ApplyAccent(_accent, isDark);
        if (_highContrast)
            ApplyHighContrastPalette();
        else
            ApplyDarkPalette(_darkBackground, isDark);
    }

    private static void ApplyHighContrastPalette()
    {
        var app = Application.Current;
        if (app is null)
            return;

        var resources = app.Resources;
        resources["WindowBackgroundBrush"] = Brush("#000000");
        resources["SurfaceBrush"] = Brush("#000000");
        resources["SurfaceAltBrush"] = Brush("#111111");
        resources["SidebarBrush"] = Brush("#000000");
        resources["BorderBrush"] = Brush("#FFFFFF");
        resources["BorderStrongBrush"] = Brush("#FFFFFF");
        resources["TextPrimaryBrush"] = Brush("#FFFFFF");
        resources["TextSecondaryBrush"] = Brush("#E6E6E6");
        resources["TextTertiaryBrush"] = Brush("#C8C8C8");
        resources["HoverBrush"] = Brush("#222222");
        resources["PressedBrush"] = Brush("#333333");
        resources["ToolbarBrush"] = Brush("#000000");
        resources["StatusBrush"] = Brush("#000000");
        resources["MenuBackgroundBrush"] = Brush("#000000");
        resources["InputBackgroundBrush"] = Brush("#000000");
        resources["ScrollThumbBrush"] = Brush("#777777");
        resources["ScrollThumbHoverBrush"] = Brush("#AAAAAA");
        resources["ErrorBrush"] = Brush("#FF6B6B");
        resources["ErrorSoftBrush"] = Brush("#330000");
        resources["WarningBrush"] = Brush("#FFD166");
        resources["WarningSoftBrush"] = Brush("#332600");
        resources["SuccessBrush"] = Brush("#6BFF9C");
        resources["OverlayBrush"] = Brush("#CC000000");
        resources["SelectionBrush"] = Brush("#554400");
        resources["AccentBrush"] = Brush("#FFD400");
        resources["AccentHoverBrush"] = Brush("#FFE45C");
        resources["AccentPressedBrush"] = Brush("#CCAA00");
        resources["AccentSoftBrush"] = Brush("#554400");
        resources["AccentBorderBrush"] = Brush("#FFD400");
        resources["AccentTextBrush"] = Brush("#000000");
    }

    private static void RemoveHighContrastOverrides()
    {
        var app = Application.Current;
        if (app is null)
            return;
        foreach (var key in HighContrastExtraKeys)
            app.Resources.Remove(key);
    }

    /// <summary>Overrides the neutral dark-theme brushes; removes them again in light mode.</summary>
    private static void ApplyDarkPalette(DarkBackground background, bool dark)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var resources = app.Resources;
        if (!dark)
        {
            foreach (var key in DarkPaletteKeys)
                resources.Remove(key);
            RemoveHighContrastOverrides();
            return;
        }

        RemoveHighContrastOverrides();
        var palette = DarkPalettes.TryGetValue(background, out var value)
            ? value
            : DarkPalettes[DarkBackground.Graphite];
        resources["WindowBackgroundBrush"] = Brush(palette.Window);
        resources["SurfaceBrush"] = Brush(palette.Surface);
        resources["SurfaceAltBrush"] = Brush(palette.SurfaceAlt);
        resources["SidebarBrush"] = Brush(palette.Sidebar);
        resources["BorderBrush"] = Brush(palette.Border);
        resources["BorderStrongBrush"] = Brush(palette.BorderStrong);
        resources["TextPrimaryBrush"] = Brush(palette.TextPrimary);
        resources["TextSecondaryBrush"] = Brush(palette.TextSecondary);
        resources["TextTertiaryBrush"] = Brush(palette.TextTertiary);
        resources["HoverBrush"] = Brush(palette.Hover);
        resources["PressedBrush"] = Brush(palette.Pressed);
        resources["ToolbarBrush"] = Brush(palette.Toolbar);
        resources["StatusBrush"] = Brush(palette.Status);
        resources["MenuBackgroundBrush"] = Brush(palette.Menu);
        resources["InputBackgroundBrush"] = Brush(palette.Input);
        resources["ScrollThumbBrush"] = Brush(palette.Scroll);
        resources["ScrollThumbHoverBrush"] = Brush(palette.ScrollHover);
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    /// <summary>
    /// Switches between the normal platform title bar and the immersive
    /// title bar. On macOS the native traffic lights stay visible while the
    /// client area is extended underneath them, matching SourceGit's approach.
    /// </summary>
    public static void ApplyWindowFrame(Window window, bool useSystemWindowFrame)
    {
        if (useSystemWindowFrame)
        {
            window.ExtendClientAreaToDecorationsHint = false;
            window.ExtendClientAreaTitleBarHeightHint = -1;
            window.WindowDecorations = WindowDecorations.Full;
            return;
        }

        // On macOS this keeps the native traffic lights, while the client
        // area renders underneath the transparent title bar. On Windows/Linux
        // Full keeps native resize, snapping and animations; MainWindow's
        // WindowDrawnDecorations theme removes the default title text and
        // caption buttons, and the app draws its own title bar instead.
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = 41;
        window.WindowDecorations = WindowDecorations.Full;
    }

    public static void ApplyAccent(AccentKind accent, bool dark)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var (accentHex, textHex) = Accents.TryGetValue(accent, out var value) ? value : Accents[AccentKind.Jade];
        var color = Color.Parse(accentHex);
        var text = Color.Parse(textHex);

        var resources = app.Resources;
        resources["AccentBrush"] = new Avalonia.Media.SolidColorBrush(color);
        resources["AccentHoverBrush"] = new Avalonia.Media.SolidColorBrush(Blend(color, Colors.White, 0.10));
        resources["AccentPressedBrush"] = new Avalonia.Media.SolidColorBrush(Blend(color, Colors.Black, 0.10));
        resources["AccentSoftBrush"] = new Avalonia.Media.SolidColorBrush(Color.FromArgb(dark ? (byte)0x3C : (byte)0x24,
            color.R, color.G, color.B));
        resources["AccentBorderBrush"] = new Avalonia.Media.SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B));
        resources["AccentTextBrush"] = new Avalonia.Media.SolidColorBrush(text);
        resources["SelectionBrush"] = resources["AccentSoftBrush"];
        resources["DropOverlayBrush"] = new Avalonia.Media.SolidColorBrush(Color.FromArgb(dark ? (byte)0x33 : (byte)0x14,
            color.R, color.G, color.B));

        resources["SystemAccentColor"] = color;
        resources["SystemAccentColorLight1"] = Blend(color, Colors.White, 0.12);
        resources["SystemAccentColorLight2"] = Blend(color, Colors.White, 0.24);
        resources["SystemAccentColorLight3"] = Blend(color, Colors.White, 0.36);
        resources["SystemAccentColorDark1"] = Blend(color, Colors.Black, 0.12);
        resources["SystemAccentColorDark2"] = Blend(color, Colors.Black, 0.24);
        resources["SystemAccentColorDark3"] = Blend(color, Colors.Black, 0.36);

        var brush = new Avalonia.Media.SolidColorBrush(color);
        resources["SystemControlHighlightAccentBrush"] = brush;
        resources["SystemControlForegroundAccentBrush"] = brush;
        resources["SystemControlBackgroundAccentBrush"] = brush;
        resources["AccentFillColorDefaultBrush"] = brush;
        resources["AccentFillColorSecondaryBrush"] = new Avalonia.Media.SolidColorBrush(Blend(color, Colors.White, 0.10));
        resources["AccentFillColorTertiaryBrush"] = new Avalonia.Media.SolidColorBrush(Blend(color, Colors.White, 0.20));
        resources["AccentTextFillColorPrimaryBrush"] = new Avalonia.Media.SolidColorBrush(text);
        resources["TextOnAccentFillColorPrimaryBrush"] = new Avalonia.Media.SolidColorBrush(text);
    }

    /// <summary>
    /// Resolves a theme resource to a brush so view models can colour icons that
    /// follow the active theme variant and accent.
    /// </summary>
    public static IBrush Brush(string resourceKey, string fallback)
    {
        if (Application.Current is { } app &&
            app.TryFindResource(resourceKey, out var value) &&
            value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallback));
    }

    public static void ApplyDensity(Window window, bool compact)
    {
        if (compact)
        {
            if (!window.Classes.Contains("compact"))
                window.Classes.Add("compact");
        }
        else
        {
            window.Classes.Remove("compact");
        }
    }

    private static Color Blend(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(a.A,
            (byte)Math.Round(a.R + (b.R - a.R) * amount),
            (byte)Math.Round(a.G + (b.G - a.G) * amount),
            (byte)Math.Round(a.B + (b.B - a.B) * amount));
    }
}
