using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.Core.Abstractions;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public enum OperationKind
{
    Copy,
    Move,
    Delete,
    Trash,
    Restore,
    EmptyTrash,
}

public enum OperationState
{
    Queued,
    Scanning,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record OperationSource(IFileSystem FileSystem, FileEntry Entry)
{
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public bool IsDirectory => Entry.IsDirectory;
}

public sealed record ConflictInfo(
    string Name,
    string SourcePath,
    string DestinationPath,
    bool SourceIsDirectory,
    bool DestinationIsDirectory,
    long SourceSize,
    long? DestinationSize);

public enum ConflictChoice
{
    Replace,
    Skip,
    Rename,
    Cancel,
    ReplaceAll,
    SkipAll,
}

public interface IConflictResolver
{
    Task<ConflictChoice> ResolveAsync(ConflictInfo info, CancellationToken cancellationToken = default);
}

public sealed partial class FileOperationItem : ObservableObject
{
    private CancellationTokenSource _cts = new();

    public FileOperationItem(string title, string detail, OperationKind kind)
    {
        Title = title;
        Detail = detail;
        Kind = kind;
        State = OperationState.Queued;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Title { get; }
    public string Detail { get; }
    public OperationKind Kind { get; }
    public string Destination { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsCancelled))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private OperationState _state;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _currentItem = string.Empty;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _etaText = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private int _successCount;

    [ObservableProperty]
    private int _failureCount;

    [ObservableProperty]
    private bool _hasWarnings;

    public bool CanCancel => State is OperationState.Queued or OperationState.Scanning or OperationState.Running;
    public bool IsCompleted => State == OperationState.Completed;
    public bool IsFailed => State == OperationState.Failed;
    public bool IsCancelled => State == OperationState.Cancelled;
    public bool IsRunning => State is OperationState.Scanning or OperationState.Running;

    public string StateText => State switch
    {
        OperationState.Queued => "等待中",
        OperationState.Scanning => "正在分析",
        OperationState.Running => "进行中",
        OperationState.Completed => "已完成",
        OperationState.Failed => "失败",
        OperationState.Cancelled => "已取消",
        _ => string.Empty,
    };

    public CancellationToken Token => _cts.Token;

    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation already finished.
        }
    }

    internal void DisposeToken()
    {
        try { _cts.Dispose(); } catch { /* ignore */ }
    }
}

public sealed class FileOperationService : IDisposable
{
    private readonly System.Threading.Channels.Channel<Job> _queue =
        System.Threading.Channels.Channel.CreateUnbounded<Job>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly Action<Action>? _post;
    private readonly TrashService _trash;

    public FileOperationService(TrashService trash, Action<Action>? uiPost = null)
    {
        _trash = trash;
        _post = uiPost;
        _worker = Task.Run(ProcessLoopAsync);
    }

    public System.Collections.ObjectModel.ObservableCollection<FileOperationItem> Operations { get; } = [];
    public IConflictResolver? ConflictResolver { get; set; }

    public event EventHandler<FileOperationItem>? Finished;

    public FileOperationItem Copy(IReadOnlyList<OperationSource> sources, IFileSystem destinationFileSystem,
        string destinationDirectory, bool move)
    {
        ValidateSources(sources);
        var destination = destinationFileSystem.GetDisplayPath(destinationDirectory);
        var title = $"{(move ? "移动" : "复制")} {sources.Count} 个项目";
        var item = new FileOperationItem(title, destination, move ? OperationKind.Move : OperationKind.Copy)
        {
            Destination = destination,
        };
        Enqueue(item, token => RunCopyAsync(item, sources, destinationFileSystem, destinationDirectory, move, token));
        return item;
    }

    public FileOperationItem Delete(IReadOnlyList<OperationSource> sources, bool permanent)
    {
        ValidateSources(sources);
        var title = permanent ? $"永久删除 {sources.Count} 个项目" : $"移到回收站 {sources.Count} 个项目";
        var item = new FileOperationItem(title, string.Empty, permanent ? OperationKind.Delete : OperationKind.Trash);
        Enqueue(item, token => RunDeleteAsync(item, sources, permanent, token));
        return item;
    }

    public FileOperationItem Restore(IReadOnlyList<TrashEntry> entries)
    {
        if (entries.Count == 0)
            throw new ArgumentException("没有要恢复的项目");

        var item = new FileOperationItem($"恢复 {entries.Count} 个项目", "回收站", OperationKind.Restore);
        Enqueue(item, token => RunRestoreAsync(item, entries, token));
        return item;
    }

    public FileOperationItem EmptyTrash()
    {
        var item = new FileOperationItem("清空回收站", "回收站", OperationKind.EmptyTrash)
        {
            IsIndeterminate = true,
        };
        Enqueue(item, async token =>
        {
            SetState(item, OperationState.Running);
            await _trash.EmptyAsync(token).ConfigureAwait(false);
            SetProgress(item, 1, true, "已清空", string.Empty, string.Empty, string.Empty);
            SetState(item, OperationState.Completed);
        });
        return item;
    }

    public void ClearFinished()
    {
        void Clear()
        {
            for (var i = Operations.Count - 1; i >= 0; i--)
            {
                if (Operations[i].State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled)
                    Operations.RemoveAt(i);
            }
        }

        if (_post is null)
            Clear();
        else
            _post(Clear);
    }

    public void CancelAll()
    {
        foreach (var item in Operations)
            item.Cancel();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        foreach (var item in Operations)
            item.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _shutdown.Dispose();
    }

    private void Enqueue(FileOperationItem item, Func<CancellationToken, Task> work)
    {
        if (_post is null)
            Operations.Insert(0, item);
        else
            _post(() => Operations.Insert(0, item));

        _queue.Writer.TryWrite(new Job(item, work));
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                var item = job.Item;
                if (item.Token.IsCancellationRequested)
                {
                    SetState(item, OperationState.Cancelled);
                    item.DisposeToken();
                    RaiseFinished(item);
                    continue;
                }

                SetState(item, OperationState.Running);
                try
                {
                    await job.Work(item.Token).ConfigureAwait(false);
                    if (item.State is OperationState.Running or OperationState.Scanning)
                        SetState(item, OperationState.Completed);
                }
                catch (OperationCanceledException)
                {
                    SetState(item, OperationState.Cancelled);
                    SetProgress(item, item.Progress, item.IsIndeterminate, "已取消", string.Empty, string.Empty, string.Empty);
                }
                catch (Exception ex)
                {
                    Log.Error($"操作失败: {item.Title}", ex);
                    SetError(item, ex.Message);
                    SetState(item, OperationState.Failed);
                }
                finally
                {
                    item.DisposeToken();
                    RaiseFinished(item);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Log.Error("操作队列异常", ex);
        }
    }

    private void RaiseFinished(FileOperationItem item)
    {
        if (_post is null)
            Finished?.Invoke(this, item);
        else
            _post(() => Finished?.Invoke(this, item));
    }

    private async Task RunCopyAsync(FileOperationItem item, IReadOnlyList<OperationSource> sources,
        IFileSystem destinationFileSystem, string destinationDirectory, bool move, CancellationToken cancellationToken)
    {
        SetState(item, OperationState.Scanning);
        SetProgress(item, 0, true, "正在分析…", string.Empty, string.Empty, string.Empty);

        var scan = await ScanSourcesAsync(sources, cancellationToken).ConfigureAwait(false);
        var tracker = new JobTracker(item, this, scan.TotalBytes, scan.TotalItems, scan.Indeterminate);
        SetState(item, OperationState.Running);
        tracker.Report(true);

        var errors = new List<string>();
        var succeeded = 0;
        var conflictContext = new ConflictContext();

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.StartSource(source.Name);

            try
            {
                var plan = await PlanDestinationAsync(source, source.Name, destinationFileSystem, destinationDirectory,
                    conflictContext, cancellationToken).ConfigureAwait(false);
                if (plan.Skip)
                    continue;

                if (source.IsDirectory && source.FileSystem.Id == destinationFileSystem.Id &&
                    IsInside(plan.Destination, source.Path, destinationFileSystem))
                {
                    errors.Add($"{source.Name}: 不能将文件夹复制到自身或其子目录");
                    continue;
                }

                var sourceHadError = false;
                if (move && source.FileSystem.Id == destinationFileSystem.Id)
                {
                    try
                    {
                        if (plan.DeleteExisting)
                            await destinationFileSystem.DeleteAsync(plan.Destination, true, cancellationToken).ConfigureAwait(false);
                        await source.FileSystem.MoveAsync(source.Path, plan.Destination, cancellationToken).ConfigureAwait(false);
                        tracker.SkipSource(source.Entry.Size, source.IsDirectory ? 0 : 1);
                        succeeded++;
                        continue;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn($"直接移动失败，回退为复制后删除: {source.Name}", ex);
                    }
                }

                if (source.IsDirectory && !source.Entry.IsSymlink)
                {
                    sourceHadError = await CopyDirectoryAsync(source, plan, destinationFileSystem, tracker, conflictContext,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (source.IsDirectory && source.Entry.IsSymlink && (!source.FileSystem.IsLocal || !destinationFileSystem.IsLocal))
                {
                    sourceHadError = true;
                    errors.Add($"{source.Name}: 无法复制远程符号链接目录");
                }
                else
                {
                    await CopyFileAsync(source, plan, destinationFileSystem, tracker, cancellationToken).ConfigureAwait(false);
                }

                if (move && !sourceHadError)
                {
                    await source.FileSystem.DeleteAsync(source.Path, source.IsDirectory, cancellationToken).ConfigureAwait(false);
                }

                if (sourceHadError)
                    errors.Add($"{source.Name}: 部分项目未能复制");
                else
                    succeeded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{source.Name}: {ex.Message}");
                Log.Warn($"复制项目失败: {source.Path}", ex);
            }
        }

        tracker.Complete(succeeded, errors);
        if (succeeded == 0 && errors.Count > 0)
            SetState(item, OperationState.Failed);
    }

    private async Task<bool> CopyDirectoryAsync(OperationSource source, DestinationPlan plan,
        IFileSystem destinationFileSystem, JobTracker tracker, ConflictContext conflictContext,
        CancellationToken cancellationToken)
    {
        var hadError = false;

        if (plan.DeleteExisting)
            await destinationFileSystem.DeleteAsync(plan.Destination, true, cancellationToken).ConfigureAwait(false);

        if (!await destinationFileSystem.ExistsAsync(plan.Destination, cancellationToken).ConfigureAwait(false))
            await destinationFileSystem.CreateDirectoryAsync(plan.Destination, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<FileEntry> children;
        try
        {
            children = await source.FileSystem.ListAsync(source.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取目录失败: {source.Path}", ex);
            tracker.FailSource();
            return true;
        }

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childSource = new OperationSource(source.FileSystem, child);
            try
            {
                var childPlan = await PlanDestinationAsync(childSource, child.Name, destinationFileSystem,
                    plan.Destination, conflictContext, cancellationToken).ConfigureAwait(false);
                if (childPlan.Skip)
                    continue;

                if (child.IsDirectory && !child.IsSymlink)
                {
                    var childHadError = await CopyDirectoryAsync(childSource, childPlan, destinationFileSystem, tracker,
                        conflictContext, cancellationToken).ConfigureAwait(false);
                    hadError |= childHadError;
                }
                else if (child.IsDirectory && child.IsSymlink &&
                         (!source.FileSystem.IsLocal || !destinationFileSystem.IsLocal))
                {
                    hadError = true;
                    tracker.FailSource();
                    Log.Warn($"跳过远程符号链接目录: {child.Path}");
                }
                else
                {
                    await CopyFileAsync(childSource, childPlan, destinationFileSystem, tracker, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                hadError = true;
                tracker.FailSource();
                Log.Warn($"复制子项目失败: {child.Path}", ex);
            }
        }

        return hadError;
    }

    private async Task CopyFileAsync(OperationSource source, DestinationPlan plan, IFileSystem destinationFileSystem,
        JobTracker tracker, CancellationToken cancellationToken)
    {
        tracker.StartItem(source.Name);
        if (plan.DeleteExisting)
            await destinationFileSystem.DeleteAsync(plan.Destination, true, cancellationToken).ConfigureAwait(false);

        if (source.Entry.IsSymlink && source.FileSystem.IsLocal && destinationFileSystem.IsLocal &&
            !string.IsNullOrEmpty(source.Entry.LinkTarget))
        {
            try
            {
                if (source.IsDirectory)
                    Directory.CreateSymbolicLink(plan.Destination, source.Entry.LinkTarget);
                else
                    File.CreateSymbolicLink(plan.Destination, source.Entry.LinkTarget);
                tracker.CompleteItem();
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"创建符号链接失败，改为复制内容: {source.Path}", ex);
            }
        }

        if (source.FileSystem.IsLocal && destinationFileSystem.IsLocal)
        {
            long previous = 0;
            var progress = new InlineProgress<long>(value =>
            {
                var delta = value - previous;
                previous = value;
                if (delta > 0)
                    tracker.AddBytes(delta);
            });
            await destinationFileSystem.CopyFileAsync(source.Path, plan.Destination, true, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await using var input = await source.FileSystem.OpenReadAsync(source.Path, cancellationToken).ConfigureAwait(false);
            await using var output = await destinationFileSystem.OpenWriteAsync(plan.Destination, true,
                source.Entry.Size > 0 ? source.Entry.Size : null, cancellationToken).ConfigureAwait(false);

            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                tracker.AddBytes(read);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (source.Entry.Modified is { } modified)
        {
            try
            {
                await destinationFileSystem.SetModifiedAsync(plan.Destination, modified, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"设置修改时间失败: {plan.Destination}", ex);
            }
        }

        if (source.Entry.UnixMode is { } mode &&
            destinationFileSystem.Capabilities.Has(FileSystemCapabilities.UnixPermissions))
        {
            try
            {
                await destinationFileSystem.SetUnixModeAsync(plan.Destination, mode, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"设置权限失败: {plan.Destination}", ex);
            }
        }

        tracker.CompleteItem();
    }

    private async Task<DestinationPlan> PlanDestinationAsync(OperationSource source, string name,
        IFileSystem destinationFileSystem, string destinationDirectory, ConflictContext context,
        CancellationToken cancellationToken)
    {
        var destination = destinationFileSystem.Combine(destinationDirectory, name);
        var sameTarget = source.FileSystem.Id == destinationFileSystem.Id &&
                         string.Equals(source.FileSystem.NormalizePath(source.Path),
                             destinationFileSystem.NormalizePath(destination), StringComparison.Ordinal);
        if (sameTarget)
            destination = await FindUniqueDestinationAsync(destinationFileSystem, destinationDirectory, name, cancellationToken)
                .ConfigureAwait(false);

        var existing = await destinationFileSystem.GetEntryAsync(destination, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return new DestinationPlan(destination, false, false);

        if (source.IsDirectory && existing.IsDirectory)
        {
            var choice = context.GlobalChoice ?? await ResolveAsync(source, destination, existing, cancellationToken).ConfigureAwait(false);
            if (choice is ConflictChoice.ReplaceAll or ConflictChoice.SkipAll)
                context.GlobalChoice = choice;
            if (choice is ConflictChoice.Skip or ConflictChoice.SkipAll)
                return new DestinationPlan(destination, false, false, true);
            if (choice is ConflictChoice.Rename)
            {
                var renamed = await FindUniqueDestinationAsync(destinationFileSystem, destinationDirectory, name, cancellationToken)
                    .ConfigureAwait(false);
                return new DestinationPlan(renamed, false, false);
            }
            if (choice is ConflictChoice.Cancel)
                throw new OperationCanceledException("用户取消了操作");
            return new DestinationPlan(destination, choice is ConflictChoice.Replace or ConflictChoice.ReplaceAll, true);
        }

        var resolved = context.GlobalChoice ?? await ResolveAsync(source, destination, existing, cancellationToken).ConfigureAwait(false);
        if (resolved is ConflictChoice.ReplaceAll or ConflictChoice.SkipAll)
            context.GlobalChoice = resolved;
        switch (resolved)
        {
            case ConflictChoice.Skip:
            case ConflictChoice.SkipAll:
                return new DestinationPlan(destination, false, false, true);
            case ConflictChoice.Replace:
            case ConflictChoice.ReplaceAll:
                return new DestinationPlan(destination, true, false, false, true);
            case ConflictChoice.Rename:
                return new DestinationPlan(await FindUniqueDestinationAsync(destinationFileSystem, destinationDirectory, name,
                    cancellationToken).ConfigureAwait(false), false, false);
            case ConflictChoice.Cancel:
                throw new OperationCanceledException("用户取消了操作");
            default:
                return new DestinationPlan(destination, false, false);
        }
    }

    private async Task<ConflictChoice> ResolveAsync(OperationSource source, string destination, FileEntry existing,
        CancellationToken cancellationToken)
    {
        if (ConflictResolver is null)
            return ConflictChoice.Rename;

        var info = new ConflictInfo(source.Name, source.Path, destination, source.IsDirectory, existing.IsDirectory,
            source.Entry.Size, existing.Size);

        if (_post is null)
            return await ConflictResolver.ResolveAsync(info, cancellationToken).ConfigureAwait(false);

        var completion = new TaskCompletionSource<ConflictChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(async () =>
        {
            try
            {
                completion.TrySetResult(await ConflictResolver.ResolveAsync(info, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return await completion.Task.ConfigureAwait(false);
    }

    private static bool IsInside(string destination, string source, IFileSystem fileSystem)
    {
        var current = fileSystem.NormalizePath(destination);
        var target = fileSystem.NormalizePath(source);
        var comparison = fileSystem.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var i = 0; i < 256; i++)
        {
            if (string.Equals(current, target, comparison))
                return true;
            var parent = fileSystem.GetParent(current);
            if (string.Equals(parent, current, comparison))
                return false;
            current = parent;
        }
        return false;
    }

    private static async Task<string> FindUniqueDestinationAsync(IFileSystem fileSystem, string directory, string name,
        CancellationToken cancellationToken)
    {
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;

        for (var i = 1; i < 10000; i++)
        {
            var candidate = i == 1 ? $"{stem} (1){extension}" : $"{stem} ({i}){extension}";
            var full = fileSystem.Combine(directory, candidate);
            if (!await fileSystem.ExistsAsync(full, cancellationToken).ConfigureAwait(false))
                return full;
        }

        return fileSystem.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private async Task RunDeleteAsync(FileOperationItem item, IReadOnlyList<OperationSource> sources,
        bool permanent, CancellationToken cancellationToken)
    {
        var tracker = new JobTracker(item, this, 0, sources.Count, false);
        SetState(item, OperationState.Running);
        var errors = new List<string>();
        var succeeded = 0;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.StartItem(source.Name);
            try
            {
                if (source.FileSystem is TrashFileSystem trashFileSystem)
                {
                    await trashFileSystem.Trash.DeletePermanentlyAsync(source.Path, cancellationToken).ConfigureAwait(false);
                }
                else if (!permanent && source.FileSystem.IsLocal && _trash.IsSupported)
                {
                    await _trash.MoveToTrashAsync(source.Path, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await source.FileSystem.DeleteAsync(source.Path, true, cancellationToken).ConfigureAwait(false);
                }

                tracker.CompleteItem();
                succeeded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{source.Name}: {ex.Message}");
                tracker.FailSource();
                Log.Warn($"删除失败: {source.Path}", ex);
            }
        }

        tracker.Report(true);
        SetSummary(item, succeeded, errors.Count, errors);
        SetProgress(item, 1, false, permanent ? "已删除" : "已移到回收站", string.Empty, string.Empty, string.Empty);
        SetState(item, errors.Count > 0 && succeeded == 0 ? OperationState.Failed : OperationState.Completed);
    }

    private async Task RunRestoreAsync(FileOperationItem item, IReadOnlyList<TrashEntry> entries,
        CancellationToken cancellationToken)
    {
        SetState(item, OperationState.Running);
        var errors = new List<string>();
        var succeeded = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            SetProgress(item, (double)i / entries.Count, false, $"正在恢复 {i + 1} / {entries.Count}", entry.Entry.Name,
                string.Empty, string.Empty);
            try
            {
                await _trash.RestoreAsync(entry, cancellationToken).ConfigureAwait(false);
                succeeded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{entry.Entry.Name}: {ex.Message}");
                Log.Warn($"恢复失败: {entry.TrashPath}", ex);
            }
        }

        SetSummary(item, succeeded, errors.Count, errors);
        SetProgress(item, 1, false, "已恢复", string.Empty, string.Empty, string.Empty);
        SetState(item, succeeded > 0 || errors.Count == 0 ? OperationState.Completed : OperationState.Failed);
    }

    private async Task<ScanResult> ScanSourcesAsync(IReadOnlyList<OperationSource> sources, CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        var totalItems = 0;
        var indeterminate = false;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.IsDirectory)
            {
                totalItems++;
                totalBytes += Math.Max(0, source.Entry.Size);
                continue;
            }

            if (source.Entry.IsSymlink)
            {
                totalItems++;
                continue;
            }

            if (source.FileSystem.IsLocal)
            {
                var result = ScanLocalDirectory(source.Path, cancellationToken);
                totalItems += result.TotalItems;
                totalBytes += result.TotalBytes;
                if (result.Indeterminate)
                    indeterminate = true;
            }
            else
            {
                // Remote directory sizes are expensive to compute; fall back to item based progress.
                indeterminate = true;
                try
                {
                    var children = await source.FileSystem.ListAsync(source.Path, cancellationToken).ConfigureAwait(false);
                    totalItems += children.Count;
                }
                catch
                {
                    totalItems++;
                }
            }
        }

        return new ScanResult(totalBytes, totalItems, indeterminate);
    }

    private static ScanResult ScanLocalDirectory(string path, CancellationToken cancellationToken)
    {
        long bytes = 0;
        var items = 0;
        var truncated = false;
        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                    RecurseSubdirectories = false,
                });
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (items > 500_000)
                {
                    truncated = true;
                    break;
                }

                try
                {
                    var attributes = File.GetAttributes(entry);
                    var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!isLink)
                            stack.Push(entry);
                    }
                    else
                    {
                        items++;
                        bytes += new FileInfo(entry).Length;
                    }
                }
                catch { /* ignore */ }
            }

            if (truncated)
                break;
        }

        return new ScanResult(bytes, items, truncated);
    }

    private sealed record ScanResult(long TotalBytes, int TotalItems, bool Indeterminate);

    private sealed record DestinationPlan(string Destination, bool DeleteExisting, bool Skip, bool Merge = false,
        bool CanMoveDirectly = false);

    private sealed record Job(FileOperationItem Item, Func<CancellationToken, Task> Work);

    private sealed class ConflictContext
    {
        public ConflictChoice? GlobalChoice { get; set; }
    }

    private void ValidateSources(IReadOnlyList<OperationSource> sources)
    {
        if (sources.Count == 0)
            throw new ArgumentException("没有要操作的项目", nameof(sources));
        if (sources.Count > 100_000)
            throw new ArgumentException("一次操作的项目过多", nameof(sources));
    }

    internal void SetState(FileOperationItem item, OperationState state)
    {
        if (_post is null)
            item.State = state;
        else
            _post(() => item.State = state);
    }

    internal void SetProgress(FileOperationItem item, double progress, bool indeterminate, string text,
        string currentItem, string speed, string eta)
    {
        void Apply()
        {
            item.Progress = Math.Clamp(progress, 0, 1);
            item.IsIndeterminate = indeterminate;
            item.ProgressText = text;
            item.CurrentItem = currentItem;
            item.SpeedText = speed;
            item.EtaText = eta;
        }

        if (_post is null)
            Apply();
        else
            _post(Apply);
    }

    internal void SetError(FileOperationItem item, string? error)
    {
        if (_post is null)
            item.Error = error;
        else
            _post(() => item.Error = error);
    }

    internal void SetSummary(FileOperationItem item, int successes, int failures, IReadOnlyList<string> errors)
    {
        var message = errors.Count > 0 ? string.Join(Environment.NewLine, errors.Take(5)) : null;
        void Apply()
        {
            item.SuccessCount = successes;
            item.FailureCount = failures;
            item.Error = message;
            item.HasWarnings = errors.Count > 0;
        }

        if (_post is null)
            Apply();
        else
            _post(Apply);
    }

    private sealed class JobTracker
    {
        private readonly FileOperationItem _item;
        private readonly FileOperationService _service;
        private readonly long _totalBytes;
        private readonly int _totalItems;
        private readonly bool _indeterminate;
        private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
        private long _doneBytes;
        private int _doneItems;
        private string _currentItem = string.Empty;
        private DateTime _lastReport = DateTime.MinValue;

        public JobTracker(FileOperationItem item, FileOperationService service, long totalBytes, int totalItems,
            bool indeterminate)
        {
            _item = item;
            _service = service;
            _totalBytes = totalBytes;
            _totalItems = totalItems;
            _indeterminate = indeterminate || (totalBytes <= 0 && totalItems <= 0);
        }

        public void StartSource(string name) => _currentItem = name;
        public void StartItem(string name) => _currentItem = name;
        public void FailSource() { }

        public void SkipSource(long bytes, int items)
        {
            _doneBytes += Math.Max(0, bytes);
            _doneItems += items;
            Report(true);
        }

        public void AddBytes(long bytes)
        {
            _doneBytes += bytes;
            if ((DateTime.UtcNow - _lastReport).TotalMilliseconds >= 120)
                Report(false);
        }

        public void CompleteItem()
        {
            _doneItems++;
            Report(false);
        }

        public void Report(bool force)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastReport).TotalMilliseconds < 120)
                return;
            _lastReport = now;

            var elapsed = _stopwatch.Elapsed.TotalSeconds;
            var speed = elapsed > 0.25 ? _doneBytes / elapsed : 0;
            var total = _totalBytes > 0 ? _totalBytes : _totalItems;
            var done = _totalBytes > 0 ? _doneBytes : _doneItems;
            var progress = total > 0 ? Math.Min(1d, (double)done / total) : 0;
            var indeterminate = _indeterminate || total <= 0;

            var progressText = _totalBytes > 0
                ? $"{Humanize.Size(_doneBytes)} / {Humanize.Size(_totalBytes)}"
                : _totalItems > 0
                    ? $"{Math.Min(_doneItems, _totalItems)} / {_totalItems}"
                    : string.Empty;

            var eta = speed > 0 && _totalBytes > _doneBytes
                ? Humanize.Duration(TimeSpan.FromSeconds((_totalBytes - _doneBytes) / speed))
                : string.Empty;

            _service.SetProgress(_item, progress, indeterminate, progressText, _currentItem,
                Humanize.Speed(speed), eta);
        }

        public void Complete(int successes, List<string> errors)
        {
            _doneBytes = Math.Max(_doneBytes, _totalBytes);
            if (_totalItems > 0)
                _doneItems = Math.Max(_doneItems, _totalItems);
            _service.SetProgress(_item, 1, false, _item.Kind == OperationKind.Move ? "已移动" : "已完成",
                string.Empty, string.Empty, string.Empty);
            _service.SetSummary(_item, successes, errors.Count, errors);
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        public InlineProgress(Action<T> action) => _action = action;
        public void Report(T value) => _action(value);
    }
}
