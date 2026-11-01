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
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
