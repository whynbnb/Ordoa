using Avalonia.Controls;
using Avalonia.Threading;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

/// <summary>Composition root for application-wide services.</summary>
public sealed class AppServices : IAsyncDisposable
{
    public AppServices()
    {
        AppPaths.EnsureCreated();
        ArchiveFileSystem.CleanupStaleCache();
        PreviewService.CleanupStaleCache();
        Settings = new SettingsService();
        Shortcuts = new ShortcutService(Settings);
        ThemeService.Apply(Settings.Current);

        Trash = new TrashService();
        TrashFs = new TrashFileSystem(Trash);
        FileSystems = new FileSystemManager();
        Recent = new RecentService();
        Bookmarks = new BookmarkService();
        Search = new SearchService();
        Session = new SessionService();
        Thumbnails = new ThumbnailService();
        Previews = new PreviewService();
        Shell = new ShellLauncher();
        OpenWith = new OpenWithService(this);
        Terminals = new TerminalService(this);
        CustomCommands = new CustomCommandService(this);
        Windows = new WindowManager(this);
        Clipboard = new ClipboardService(() => Windows.ActiveWindow);
        Dialogs = new DialogService(() => Windows.ActiveWindow);
        Integration = new SystemIntegrationService();
        FileManager1 = new FileManager1Service((uris, action) => Windows.HandleFileManagerRequestAsync(uris, action));
        Operations = new FileOperationService(Trash, action => Dispatcher.UIThread.Post(action), Settings);
        Operations.ConflictResolver = new DialogConflictResolver(Dialogs);
        Undo = new UndoService(this);
    }

    public SettingsService Settings { get; }
    public ShortcutService Shortcuts { get; }
    public FileSystemManager FileSystems { get; }
    public TrashService Trash { get; }
    public TrashFileSystem TrashFs { get; }
    public FileOperationService Operations { get; }
    public SearchService Search { get; }
    public RecentService Recent { get; }
    public BookmarkService Bookmarks { get; }
    public SessionService Session { get; }
    public ThumbnailService Thumbnails { get; }
    public PreviewService Previews { get; }
    public ClipboardService Clipboard { get; }
    public ShellLauncher Shell { get; }
    public OpenWithService OpenWith { get; }
    public TerminalService Terminals { get; }
    public CustomCommandService CustomCommands { get; }
    public WindowManager Windows { get; }
    public SystemIntegrationService Integration { get; }
    public FileManager1Service FileManager1 { get; }
    public UndoService Undo { get; }
    public DialogService Dialogs { get; }
    public Window? MainWindow => Windows.ActiveWindow;

    public async Task StartIntegrationAsync()
    {
        if (Settings.Current.SingleInstance)
        {
            try
            {
                SingleInstanceService.StartServer(Windows.HandleActivationAsync);
            }
            catch (Exception ex)
            {
                Log.Warn("启动单实例服务失败", ex);
            }
        }

        if (!AppPaths.IsLinux)
            return;

        try
        {
            if (Program.IsFileManagerService || await Integration.IsDefaultFileManagerAsync().ConfigureAwait(false))
                await FileManager1.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("启动系统文件管理器集成失败", ex);
        }
    }

    public async Task<bool> StartFileManagerServiceAsync(bool force = false)
    {
        if (!AppPaths.IsLinux)
            return false;

        if (!force && !await Integration.IsDefaultFileManagerAsync().ConfigureAwait(false))
            return false;

        return await FileManager1.StartAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { Operations.Dispose(); } catch { /* ignore */ }
        try { Thumbnails.Clear(); } catch { /* ignore */ }
        try { await SingleInstanceService.StopAsync().ConfigureAwait(false); } catch { /* ignore */ }
        try { await FileManager1.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        await FileSystems.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class DialogConflictResolver : IConflictResolver
    {
        private readonly IDialogService _dialogs;
        public DialogConflictResolver(IDialogService dialogs) => _dialogs = dialogs;

        public Task<ConflictChoice> ResolveAsync(ConflictInfo info, CancellationToken cancellationToken = default)
            => _dialogs.ResolveConflictAsync(info, cancellationToken);
    }
}
