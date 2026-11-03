using Avalonia.Controls;
using Avalonia.Media;

namespace Ordoa.App.Services;

/// <summary>
/// Applies consistent text-rendering options to every Ordoa window. At
/// fractional display scales subpixel antialiasing produces colour fringes
/// that make the UI look soft, so use grayscale antialiasing with an aligned
/// baseline and light hinting instead.
/// </summary>
public static class WindowAppearance
{
    public static void Apply(Window window)
    {
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
        TextOptions.SetTextHintingMode(window, TextHintingMode.Light);
        TextOptions.SetBaselinePixelAlignment(window, BaselinePixelAlignment.Aligned);
    }
}
