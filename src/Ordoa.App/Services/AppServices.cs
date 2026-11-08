using Avalonia.Controls;
using Avalonia.Threading;
using Ordoa.Core.Abstractions;
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
        Journal = new TransferJournal();
        Recent = new RecentService();
        Bookmarks = new BookmarkService();
        Search = new SearchService();
        Session = new SessionService();
        Workspaces = new WorkspaceService();
        Tags = new TagService();
        RemoteEdits = new RemoteEditService();
        TabDrags = new TabDragService();
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
        Operations = new FileOperationService(Trash, action => Dispatcher.UIThread.Post(action), Settings, Journal);
        Operations.ConflictResolver = new DialogConflictResolver(Dialogs);
        Operations.Finished += OnOperationFinishedForTags;
        Operations.Finished += OnOperationFinishedForResume;
        Undo = new UndoService(this);
    }

    public SettingsService Settings { get; }
    public ShortcutService Shortcuts { get; }
    public FileSystemManager FileSystems { get; }
    public TrashService Trash { get; }
    public TrashFileSystem TrashFs { get; }
    public FileOperationService Operations { get; }
    public TransferJournal Journal { get; }
    public SearchService Search { get; }
    public RecentService Recent { get; }
    public BookmarkService Bookmarks { get; }
    public SessionService Session { get; }
    public WorkspaceService Workspaces { get; }
    public TagService Tags { get; }
    public RemoteEditService RemoteEdits { get; }
    public TabDragService TabDrags { get; }
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

    /// <summary>
    /// Requeues copy/move requests that were still pending when the previous
    /// process exited. Completed items are removed from the journal as their
    /// resumed operation finishes.
    /// </summary>
    public async Task<int> ResumePendingTransfersAsync(CancellationToken cancellationToken = default)
    {
        var pending = Journal.Items;
        if (pending.Count == 0)
            return 0;

        var resumed = 0;
        foreach (var record in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var destination = await ResolveFileSystemAsync(record.DestinationFileSystemId, cancellationToken)
                    .ConfigureAwait(false);
                var sources = new List<OperationSource>();
                foreach (var source in record.Sources)
                {
                    var fileSystem = await ResolveFileSystemAsync(source.FileSystemId, cancellationToken)
                        .ConfigureAwait(false);
                    var entry = await fileSystem.GetEntryAsync(source.Path, cancellationToken).ConfigureAwait(false);
                    if (entry is not null)
                        sources.Add(new OperationSource(fileSystem, entry));
                }

                if (sources.Count == 0)
                {
                    Journal.Remove(record.Id);
                    continue;
                }

                var item = Operations.Copy(sources, destination, record.DestinationPath, record.Move,
                    ConflictChoice.ReplaceAll, journal: false);
                lock (_resumedTransfers)
                    _resumedTransfers[item.Id] = record.Id;
                resumed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"恢复传输失败: {record.Id}", ex);
                Journal.Remove(record.Id);
            }
        }

        return resumed;
    }

    private readonly Dictionary<string, string> _resumedTransfers = new(StringComparer.Ordinal);

    private void OnOperationFinishedForResume(object? sender, FileOperationItem item)
    {
        string? journalId;
        lock (_resumedTransfers)
        {
            if (!_resumedTransfers.Remove(item.Id, out journalId))
                return;
        }

        Journal.Remove(journalId);
    }

    private Task<IFileSystem> ResolveFileSystemAsync(string id, CancellationToken cancellationToken)
        => id == FileSystems.Local.Id
            ? Task.FromResult(FileSystems.Local)
            : FileSystems.ConnectByIdAsync(id, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try { Operations.Dispose(); } catch { /* ignore */ }
        try { Thumbnails.Clear(); } catch { /* ignore */ }
        try { RemoteEdits.Dispose(); } catch { /* ignore */ }
        try { await SingleInstanceService.StopAsync().ConfigureAwait(false); } catch { /* ignore */ }
        try { await FileManager1.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        await FileSystems.DisposeAsync().ConfigureAwait(false);
    }

    private void OnOperationFinishedForTags(object? sender, FileOperationItem item)
    {
        try
        {
            foreach (var result in item.Results)
            {
                if (item.Kind == OperationKind.Copy && result.DestinationFileSystem is not null &&
                    result.Destination is not null)
                {
                    Tags.CopyTree(result.Source.FileSystem, result.Source.Path,
                        result.DestinationFileSystem, result.Destination);
                }
                else if (item.Kind == OperationKind.Move && result.DestinationFileSystem is not null &&
                         result.Destination is not null)
                {
                    Tags.MoveTree(result.Source.FileSystem, result.Source.Path,
                        result.DestinationFileSystem, result.Destination);
                }
                else if (item.Kind == OperationKind.Delete)
                {
                    Tags.RemoveTree(result.Source.FileSystem, result.Source.Path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("同步文件标签失败", ex);
        }
    }

    private sealed class DialogConflictResolver : IConflictResolver
    {
        private readonly IDialogService _dialogs;
        public DialogConflictResolver(IDialogService dialogs) => _dialogs = dialogs;

        public Task<ConflictChoice> ResolveAsync(ConflictInfo info, CancellationToken cancellationToken = default)
            => _dialogs.ResolveConflictAsync(info, cancellationToken);
    }
}
