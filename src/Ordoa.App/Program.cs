using Avalonia;
using System;

namespace Ordoa.App;

internal static class Program
{
    public static string[] StartupArguments { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        StartupArguments = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

        // 在 Wayland 会话中优先使用原生 Wayland 后端；
        // 没有可用的合成器或非 Linux 平台时自动回退到 X11/Win32/macOS。
        if (Environment.GetEnvironmentVariable("ORDOA_NO_WAYLAND") is null)
            builder = builder.UseWaylandWithFallback();

        return builder
            .WithInterFont()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
    }
}
