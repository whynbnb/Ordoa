using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Ordoa.App.Views;

/// <summary>
/// Helpers for showing native popups.
/// </summary>
internal static class PopupHelper
{
    /// <summary>
    /// Opens a context menu, working around a sizing quirk of the experimental
    /// native Wayland backend.
    /// </summary>
    /// <remarks>
    /// On Wayland the native popup is sized from the menu's first layout pass,
    /// before its control theme has been applied. A short menu is therefore
    /// created smaller than its final content and shows a scrollbar even when
    /// the content would fit. Opening once invisibly applies the theme and lets
    /// the menu measure itself; if the popup is still too short, reopen it so
    /// the final size is used.
    /// </remarks>
    public static async void OpenContextMenu(ContextMenu menu, Control target)
    {
        if (!IsNativeWayland(target))
        {
            menu.Open(target);
            return;
        }

        try
        {
            menu.Opacity = 0;
            menu.Open(target);
            await Task.Delay(40);

            if (!menu.IsOpen)
                return;

            if (menu.Bounds.Height + 0.5 >= menu.DesiredSize.Height)
            {
                menu.Opacity = 1;
                return;
            }

            menu.Close();
            menu.Opacity = 1;
            menu.Open(target);
        }
        catch
        {
            menu.Opacity = 1;
            if (!menu.IsOpen)
                menu.Open(target);
            else
                Dispatcher.UIThread.Post(menu.Close);
        }
    }

    private static bool IsNativeWayland(Control target)
    {
        // X11 reports "XID"; the Wayland backend exposes no platform handle.
        return OperatingSystem.IsLinux()
            && string.IsNullOrEmpty(TopLevel.GetTopLevel(target)?.TryGetPlatformHandle()?.HandleDescriptor);
    }
}
