using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ordoa.App.Services;
using Ordoa.App.ViewModels;
using Ordoa.App.Views;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;

namespace Ordoa.App;

public partial class App : Application
{
    public static AppServices? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // A tooltip appearing while the pointer is captured by drag-and-drop
        // makes the native drag cursor flicker on some platforms.
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((_, e) =>
        {
            if (FileDragContext.IsDragging)
                e.Handled = true;
        });

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("UI 线程未处理异常", e.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            Services = services;
            L10n.SetLanguage(services.Settings.Current.Language);

            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            services.Windows.AttachLifetime(desktop);
            var window = services.Windows.CreateWindow(restoreSession: true);
            desktop.MainWindow = window;
            _ = services.StartIntegrationAsync();

            desktop.Exit += async (_, _) =>
            {
                try
                {
                    await services.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log.Error("释放资源失败", ex);
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
