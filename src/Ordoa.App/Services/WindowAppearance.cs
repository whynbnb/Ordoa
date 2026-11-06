using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    public static void Apply(Window window, bool guardInitialKeys = true)
    {
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
        TextOptions.SetTextHintingMode(window, TextHintingMode.Light);
        TextOptions.SetBaselinePixelAlignment(window, BaselinePixelAlignment.Aligned);

        if (!guardInitialKeys)
            return;

        // A dialog can be opened by the same Enter/Escape press that is still
        // being routed. Ignore the first moments of keyboard input so a fresh
        // dialog cannot immediately activate its default button or close.
        var openedAt = 0L;
        window.Opened += (_, _) => openedAt = Environment.TickCount64;

        void Guard(object? _, KeyEventArgs e)
        {
            if (openedAt != 0 && Environment.TickCount64 - openedAt < 180 &&
                e.Key is Key.Enter or Key.Escape or Key.Space)
            {
                e.Handled = true;
            }
        }

        window.AddHandler(InputElement.KeyDownEvent, Guard, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyUpEvent, Guard, RoutingStrategies.Tunnel);
    }
}
