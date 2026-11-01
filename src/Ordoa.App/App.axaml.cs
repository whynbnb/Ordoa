using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ordoa.App.Services;
using Ordoa.App.ViewModels;
using Ordoa.App.Views;
using Ordoa.Core.Services;

namespace Ordoa.App;

public partial class App : Application
{
    public static AppServices? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
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

            var viewModel = new MainViewModel(services);
            var window = new MainWindow
            {
                DataContext = viewModel,
            };
            services.AttachWindow(window);
            desktop.MainWindow = window;

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
