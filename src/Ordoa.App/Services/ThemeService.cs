using Avalonia.Media;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Styling;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

public static class ThemeService
{
    private static readonly Dictionary<AccentKind, (string Base, string Text)> Accents = new()
    {
        [AccentKind.Jade] = ("#17937F", "#FFFFFF"),
        [AccentKind.Blue] = ("#3B7EDB", "#FFFFFF"),
        [AccentKind.Amber] = ("#C9862B", "#241A05"),
        [AccentKind.Rose] = ("#C2688A", "#FFFFFF"),
        [AccentKind.Violet] = ("#7C63D8", "#FFFFFF"),
    };

    public static void Apply(AppSettings settings)
    {
        var app = Application.Current;
        if (app is null)
            return;

        app.RequestedThemeVariant = settings.Theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        var isDark = app.ActualThemeVariant == ThemeVariant.Dark;
        ApplyAccent(settings.Accent, isDark);
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
