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
        Settings = new SettingsService();
        ThemeService.Apply(Settings.Current);

        Trash = new TrashService();
        TrashFs = new TrashFileSystem(Trash);
        FileSystems = new FileSystemManager();
        Recent = new RecentService();
        Bookmarks = new BookmarkService();
        Search = new SearchService();
        Session = new SessionService();
        Thumbnails = new ThumbnailService();
        Shell = new ShellLauncher();
        Clipboard = new ClipboardService(() => MainWindow);
        Dialogs = new DialogService(() => MainWindow);
        Operations = new FileOperationService(Trash, action => Dispatcher.UIThread.Post(action));
        Operations.ConflictResolver = new DialogConflictResolver(Dialogs);
    }

    public SettingsService Settings { get; }
    public FileSystemManager FileSystems { get; }
    public TrashService Trash { get; }
    public TrashFileSystem TrashFs { get; }
    public FileOperationService Operations { get; }
    public SearchService Search { get; }
    public RecentService Recent { get; }
    public BookmarkService Bookmarks { get; }
    public SessionService Session { get; }
    public ThumbnailService Thumbnails { get; }
    public ClipboardService Clipboard { get; }
    public ShellLauncher Shell { get; }
    public DialogService Dialogs { get; }
    public Window? MainWindow { get; private set; }

    public void AttachWindow(Window window)
    {
        MainWindow = window;
        ThemeService.ApplyDensity(window, Settings.Current.CompactMode);
    }

    public async ValueTask DisposeAsync()
    {
        try { Operations.Dispose(); } catch { /* ignore */ }
        try { Thumbnails.Clear(); } catch { /* ignore */ }
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
