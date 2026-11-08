using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
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
    private static NativeMenu? _macAppMenu;
    private static NativeMenuItem? _macAboutItem;
    private static NativeMenuItem? _macSettingsItem;
    private static NativeMenuItem? _macServicesItem;
    private static NativeMenuItem? _macHideItem;
    private static NativeMenuItem? _macHideOthersItem;
    private static NativeMenuItem? _macShowAllItem;
    private static NativeMenuItem? _macQuitItem;

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

        // The macOS native menu exporter is created after Initialize(), and it
        // reads the application menu from NativeMenu.GetMenu(Application).
        // Supplying it here lets the exporter use our menu instead of creating
        // the default one that contains "About Avalonia".
        InstallMacOSAppMenu();
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

            CaptureMacOSStandardMenuItems();
            RefreshMacOSAppMenu();
            L10n.LanguageChanged += RefreshMacOSAppMenu;

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

    /// <summary>
    /// Replaces Avalonia's built-in "About Avalonia" app-menu item with the
    /// Ordoa about window. Avalonia appends the remaining standard macOS items
    /// (Services, Hide, Hide Others, Show All, Quit) when the menu is exported,
    /// so only the first item needs to be supplied here.
    /// </summary>
    private static void InstallMacOSAppMenu()
    {
        if (!OperatingSystem.IsMacOS() || Application.Current is not { } app || _macAppMenu is not null)
            return;

        _macAppMenu = new NativeMenu();
        _macAboutItem = new NativeMenuItem(L10n.T("关于 Ordoa"));
        _macAboutItem.Click += (_, _) =>
        {
            if (Services is not null)
                _ = Services.Dialogs.ShowAboutAsync();
        };
        _macAppMenu.Add(_macAboutItem);

        _macSettingsItem = new NativeMenuItem(L10n.T("设置…"))
        {
            Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta),
        };
        _macSettingsItem.Click += (_, _) =>
        {
            if (Services?.Windows.ActiveWindow?.DataContext is MainViewModel viewModel)
                _ = viewModel.ShowSettingsAsync();
        };
        _macAppMenu.Add(_macSettingsItem);

        NativeMenu.SetMenu(app, _macAppMenu);
    }

    /// <summary>
    /// Avalonia appends the standard macOS application-menu items when the
    /// native menu is first exported. Keep references to them so their labels
    /// can follow the selected UI language; the click handlers and gestures
    /// stay untouched.
    /// </summary>
    private static void CaptureMacOSStandardMenuItems()
    {
        if (_macAppMenu is null)
            return;

        foreach (var item in _macAppMenu.Items.OfType<NativeMenuItem>())
        {
            switch (item.Header)
            {
                case "Services":
                    _macServicesItem = item;
                    break;
                case "Hide Others":
                    _macHideOthersItem = item;
                    break;
                case "Show All":
                    _macShowAllItem = item;
                    break;
                case "Quit":
                    _macQuitItem = item;
                    break;
                default:
                    if (_macHideItem is null && item.Header?.StartsWith("Hide ", StringComparison.Ordinal) == true)
                        _macHideItem = item;
                    break;
            }
        }
    }

    private static void RefreshMacOSAppMenu()
    {
        if (_macAppMenu is null)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshMacOSAppMenu);
            return;
        }

        if (_macAboutItem is not null)
            _macAboutItem.Header = L10n.T("关于 Ordoa");
        if (_macSettingsItem is not null)
            _macSettingsItem.Header = L10n.T("设置…");
        if (_macServicesItem is not null)
            _macServicesItem.Header = L10n.T("服务");
        if (_macHideItem is not null)
            _macHideItem.Header = L10n.T("隐藏 Ordoa");
        if (_macHideOthersItem is not null)
            _macHideOthersItem.Header = L10n.T("隐藏其他");
        if (_macShowAllItem is not null)
            _macShowAllItem.Header = L10n.T("全部显示");
        if (_macQuitItem is not null)
            _macQuitItem.Header = L10n.T("退出 Ordoa");
    }
}
