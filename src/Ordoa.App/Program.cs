using Avalonia;
using Ordoa.App.Services;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App;

internal static class Program
{
    public static string[] StartupArguments { get; private set; } = [];
    public static bool IsFileManagerService { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var settings = JsonFile.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
        var request = ActivationRequest.Parse(args);
        L10n.SetLanguage(settings.Language);

        if (request.RegisterFileManager)
        {
            var integration = new SystemIntegrationService();
            var result = integration.RegisterAsDefaultFileManagerAsync().GetAwaiter().GetResult();
            Console.WriteLine(result.Message);
            return result.Success ? 0 : 1;
        }

        IsFileManagerService = request.FileManagerService;

        if (settings.SingleInstance && !request.NoSingleInstance && !request.FileManagerService)
        {
            if (!SingleInstanceService.TryAcquireServer())
            {
                if (SingleInstanceService.TryForward(request, TimeSpan.FromSeconds(2)))
                    Environment.Exit(0);

                // The other process may still be starting its accept loop.
                Thread.Sleep(250);
                if (SingleInstanceService.TryForward(request, TimeSpan.FromSeconds(2)))
                    Environment.Exit(0);

                // Do not drop the request if the existing process is unresponsive.
                SingleInstanceService.TryAcquireServer();
            }
        }
        else if (settings.SingleInstance)
        {
            SingleInstanceService.TryAcquireServer();
        }

        StartupArguments = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
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
