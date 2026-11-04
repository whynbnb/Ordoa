using Ordoa.App.ViewModels;
using Ordoa.Core.Abstractions;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

public enum UndoEntryKind
{
    Rename,
    Move,
    Trash,
    New,
    Copy,
}

public sealed class UndoEntry
{
    public required UndoEntryKind Kind { get; init; }
    public required string Title { get; init; }
    public required Func<Task<bool>> Undo { get; init; }
    public required Func<Task<bool>> Redo { get; init; }
}

/// <summary>
/// Session-scoped file operation history. The history is intentionally not
/// persisted: it reflects only what happened in this process.
/// </summary>
public sealed class UndoService
{
    private const int MaxHistory = 50;
    private readonly AppServices _services;
    private readonly List<UndoEntry> _undo = [];
    private readonly List<UndoEntry> _redo = [];
    private bool _applying;

    public UndoService(AppServices services)
    {
        _services = services;
        services.Operations.Finished += OnOperationFinished;
    }

    public event EventHandler? Changed;

    public int Count => _undo.Count;
    public int RedoCount => _redo.Count;
    public string? UndoTitle => _undo.Count == 0 ? null : _undo[^1].Title;
    public string? RedoTitle => _redo.Count == 0 ? null : _redo[^1].Title;
    public bool CanUndo => !_applying && EffectiveBehavior != UndoBehavior.Disabled && _undo.Count > 0;
    public bool CanRedo => !_applying && EffectiveBehavior != UndoBehavior.Disabled && _redo.Count > 0;

    private UndoBehavior EffectiveBehavior => _services.Settings.Current.UndoBehavior switch
    {
        UndoBehavior.System => AppPaths.IsLinux ? UndoBehavior.StructureOnly : UndoBehavior.Full,
        var value => value,
    };

    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0)
            return;

        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordRename(IFileSystem fileSystem, string oldPath, string newPath, FileEntry snapshot,
        string? title = null)
    {
        var record = new RenameRecord(
            fileSystem,
            fileSystem.NormalizePath(oldPath),
            fileSystem.NormalizePath(newPath),
            snapshot);

        Record(new UndoEntry
        {
            Kind = UndoEntryKind.Rename,
            Title = string.IsNullOrWhiteSpace(title) ? L10n.T("重命名") : title,
            Undo = () => UndoRenamesAsync([record]),
            Redo = () => RedoRenamesAsync([record]),
        });
    }

    public void RecordBatchRename(IFileSystem fileSystem, IReadOnlyList<BatchRenameOperation> operations,
        string? title = null)
    {
        if (operations.Count == 0)
            return;

        var records = operations
            .Select(operation => new RenameRecord(
                fileSystem,
                fileSystem.NormalizePath(operation.Entry.Path),
                fileSystem.NormalizePath(fileSystem.Combine(
                    fileSystem.GetParent(operation.Entry.Path), operation.NewName)),
                operation.Entry))
            .ToList();

        Record(new UndoEntry
        {
            Kind = UndoEntryKind.Rename,
            Title = string.IsNullOrWhiteSpace(title)
                ? L10n.Format("批量重命名 {0} 个项目", records.Count)
                : title,
            Undo = () => UndoRenamesAsync(records),
            Redo = () => RedoRenamesAsync(records),
        });
    }

    public void RecordNew(IFileSystem fileSystem, string path, FileEntry snapshot, string? title = null)
    {
        var record = new NewRecord(fileSystem, fileSystem.NormalizePath(path), snapshot);
        Record(new UndoEntry
        {
            Kind = UndoEntryKind.New,
            Title = string.IsNullOrWhiteSpace(title) ? L10n.T("新建") : title,
            Undo = () => UndoNewAsync(record),
            Redo = () => RedoNewAsync(record),
        });
    }

    public async Task UndoAsync()
    {
        if (!CanUndo)
            return;

        var entry = _undo[^1];
        _applying = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            bool success;
            try
            {
                success = await entry.Undo().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("撤销操作失败", ex);
                await _services.Dialogs.ShowErrorAsync(L10n.T("无法撤销"), ex.Message).ConfigureAwait(true);
                return;
            }

            if (!success)
                return;

            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(entry);
            await RefreshAsync().ConfigureAwait(true);
            Notify(L10n.T("已撤销"), entry.Title, false);
        }
        finally
        {
            _applying = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RedoAsync()
    {
        if (!CanRedo)
            return;

        var entry = _redo[^1];
        _applying = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            bool success;
            try
            {
                success = await entry.Redo().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("重做操作失败", ex);
                await _services.Dialogs.ShowErrorAsync(L10n.T("无法重做"), ex.Message).ConfigureAwait(true);
                return;
            }

            if (!success)
                return;

            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(entry);
            await RefreshAsync().ConfigureAwait(true);
            Notify(L10n.T("已重做"), entry.Title, false);
        }
        finally
        {
            _applying = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Record(UndoEntry entry)
    {
        if (EffectiveBehavior == UndoBehavior.Disabled || 
            (entry.Kind == UndoEntryKind.Copy && EffectiveBehavior != UndoBehavior.Full))
        {
            _redo.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _undo.Add(entry);
        while (_undo.Count > MaxHistory)
            _undo.RemoveAt(0);

        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateRedo()
    {
        if (_redo.Count == 0)
            return;

        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnOperationFinished(object? sender, FileOperationItem item)
    {
        if (item.Results.Count == 0)
        {
            if (item.Kind is OperationKind.Delete or OperationKind.Restore && item.SuccessCount > 0)
                Clear();
            else if (item.Kind == OperationKind.EmptyTrash && item.State == OperationState.Completed)
                Clear();
            return;
        }

        if ((item.Kind is OperationKind.Copy or OperationKind.Move) &&
            item.Results.Any(result => result.ReplacedExisting))
        {
            // The original target was overwritten, so it cannot be restored by
            // an inverse operation. Do not let Ctrl+Z silently undo older work.
            Clear();
            return;
        }

        if (item.Kind == OperationKind.Copy && EffectiveBehavior != UndoBehavior.Full)
        {
            Clear();
            return;
        }

        InvalidateRedo();

        switch (item.Kind)
        {
            case OperationKind.Copy:
                RecordCopy(item.Results);
                break;
            case OperationKind.Move:
                RecordMove(item.Results);
                break;
            case OperationKind.Trash:
                RecordTrash(item.Results);
                break;
        }
    }

    private void RecordCopy(IReadOnlyList<FileOperationResult> results)
    {
        var records = results
            .Where(result => !result.ReplacedExisting &&
                             result.DestinationFileSystem is not null && result.Destination is not null)
            .Select(result => new CopyRecord(
                result.Source.FileSystem,
                result.Source.Entry,
                result.Source.FileSystem.NormalizePath(result.Source.Path),
                result.DestinationFileSystem!,
                result.DestinationFileSystem!.NormalizePath(result.Destination!),
                result.DestinationEntry ?? (result.Source.IsDirectory ? null : result.Source.Entry)))
            .ToList();

        if (records.Count == 0)
            return;

        Record(new UndoEntry
        {
            Kind = UndoEntryKind.Copy,
            Title = L10n.Format("复制 {0} 个项目", records.Count),
            Undo = () => UndoCopyAsync(records),
            Redo = () => RedoCopyAsync(records),
        });
    }

    private void RecordMove(IReadOnlyList<FileOperationResult> results)
    {
        var records = results
            .Where(result => !result.ReplacedExisting &&
                             result.DestinationFileSystem is not null && result.Destination is not null)
            .Select(result => new MoveRecord(
                result.Source.FileSystem,
                result.Source.Entry,
                result.Source.FileSystem.NormalizePath(result.Source.Path),
                result.DestinationFileSystem!,
                result.DestinationFileSystem!.NormalizePath(result.Destination!),
                result.DestinationEntry ?? result.Source.Entry))
            .ToList();

        if (records.Count == 0)
            return;

        Record(new UndoEntry
        {
            Kind = UndoEntryKind.Move,
            Title = L10n.Format("移动 {0} 个项目", records.Count),
            Undo = () => UndoMovesAsync(records),
            Redo = () => RedoMovesAsync(records),
        });
    }

    private void RecordTrash(IReadOnlyList<FileOperationResult> results)
    {
        var records = results
            .Where(result => result.TrashEntry is not null)
            .Select(result => new TrashRecord(result.Source, result.TrashEntry!))
            .ToList();

        if (records.Count == 0)
            return;

        Record(new UndoEntry
        {
            Kind = UndoEntryKind.Trash,
            Title = L10n.Format("移到回收站 {0} 个项目", records.Count),
            Undo = () => UndoTrashAsync(records),
            Redo = () => RedoTrashAsync(records),
        });
    }

    private async Task<bool> UndoCopyAsync(List<CopyRecord> records)
    {
        const string title = "无法撤销复制";
        var active = records.Where(record => record.Applied).ToList();
        foreach (var record in active)
        {
            if (!await MatchesAsync(record.DestinationFileSystem, record.DestinationPath, record.DestinationEntry)
                    .ConfigureAwait(true))
            {
                return await ConflictAsync(L10n.T(title),
                    L10n.Format("复制后的项目已被修改或删除：{0}", record.DestinationPath)).ConfigureAwait(true);
            }
        }

        var errors = new List<string>();
        foreach (var record in active)
        {
            try
            {
                await record.DestinationFileSystem
                    .DeleteAsync(record.DestinationPath, true, CancellationToken.None).ConfigureAwait(true);
                record.Applied = false;
            }
            catch (Exception ex)
            {
                errors.Add($"{record.DestinationPath}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
            return true;

        await _services.Dialogs.ShowErrorAsync(L10n.T("撤销复制时发生错误"),
            string.Join(Environment.NewLine, errors.Take(8))).ConfigureAwait(true);
        return false;
    }

    private async Task<bool> RedoCopyAsync(List<CopyRecord> records)
    {
        const string title = "无法重做复制";
        var pending = records.Where(record => !record.Applied).ToList();
        foreach (var record in pending)
        {
            var source = await TryGetEntryAsync(record.SourceFileSystem, record.SourcePath).ConfigureAwait(true);
            if (source is null)
            {
                return await ConflictAsync(L10n.T(title),
                    L10n.Format("源项目已不存在：{0}", record.SourcePath)).ConfigureAwait(true);
            }

            if (!await EnsureFreeAsync(record.DestinationFileSystem, record.DestinationPath, L10n.T(title))
                    .ConfigureAwait(true))
            {
                return false;
            }

            if (!await EnsureParentAsync(record.DestinationFileSystem, record.DestinationPath, L10n.T(title))
                    .ConfigureAwait(true))
            {
                return false;
            }
        }

        var errors = new List<string>();
        foreach (var record in pending)
        {
            try
            {
                var source = await record.SourceFileSystem.GetEntryAsync(record.SourcePath).ConfigureAwait(true)
                             ?? throw new FileNotFoundException(L10n.T("源项目已不存在"), record.SourcePath);
                await CopyEntryAsync(record.SourceFileSystem, source, record.SourcePath,
                    record.DestinationFileSystem, record.DestinationPath, CancellationToken.None).ConfigureAwait(true);
                record.DestinationEntry = await TryGetEntryAsync(record.DestinationFileSystem, record.DestinationPath)
                    .ConfigureAwait(true) ?? source;
                record.Applied = true;
            }
            catch (Exception ex)
            {
                try
                {
                    if (await record.DestinationFileSystem.ExistsAsync(record.DestinationPath).ConfigureAwait(true))
                    {
                        await record.DestinationFileSystem
                            .DeleteAsync(record.DestinationPath, true, CancellationToken.None).ConfigureAwait(true);
                    }
                }
                catch { /* best effort cleanup */ }

                errors.Add($"{record.DestinationPath}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
            return true;

        await _services.Dialogs.ShowErrorAsync(L10n.T("重做复制时发生错误"),
            string.Join(Environment.NewLine, errors.Take(8))).ConfigureAwait(true);
        return false;
    }

    private async Task<bool> UndoMovesAsync(List<MoveRecord> records)
    {
        const string title = "无法撤销移动";
        var active = records.Where(record => record.Applied).ToList();
        foreach (var record in active)
        {
            if (!await MatchesAsync(record.DestinationFileSystem, record.DestinationPath, record.DestinationEntry)
                    .ConfigureAwait(true))
            {
                return await ConflictAsync(L10n.T(title),
                    L10n.Format("移动后的项目已被修改或删除：{0}", record.DestinationPath)).ConfigureAwait(true);
            }

            if (!await EnsureFreeAsync(record.SourceFileSystem, record.SourcePath, L10n.T(title))
                    .ConfigureAwait(true))
            {
                return false;
            }

            if (!await EnsureParentAsync(record.SourceFileSystem, record.SourcePath, L10n.T(title))
                    .ConfigureAwait(true))
            {
                return false;
            }
        }

        var errors = new List<string>();
        foreach (var record in active)
        {
            try
            {
                await MoveEntryAsync(record.DestinationFileSystem, record.DestinationPath,
                    record.SourceFileSystem, record.SourcePath, CancellationToken.None).ConfigureAwait(true);
                record.SourceEntry = await TryGetEntryAsync(record.SourceFileSystem, record.SourcePath)
                    .ConfigureAwait(true) ?? record.SourceEntry;
                record.Applied = false;
            }
            catch (Exception ex)
            {
                errors.Add($"{record.DestinationPath}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
            return true;

        await _services.Dialogs.ShowErrorAsync(L10n.T("撤销移动时发生错误"),
            string.Join(Environment.NewLine, errors.Take(8))).ConfigureAwait(true);
        return false;
    }

    private async Task<bool> RedoMovesAsync(List<MoveRecord> records)
    {
        const string title = "无法重做移动";
        var pending = records.Where(record => !record.Applied).ToList();
        foreach (var record in pending)
        {
            if (!await MatchesAsync(record.SourceFileSystem, record.SourcePath, record.SourceEntry).ConfigureAwait(true))
            {
                return await ConflictAsync(L10n.T(title),
                    L10n.Format("原位置的项目已被修改或删除：{0}", record.SourcePath)).ConfigureAwait(true);
            }

            if (!await EnsureFreeAsync(record.DestinationFileSystem, record.DestinationPath, L10n.T(title))
                    .ConfigureAwait(true))
            {
                return false;
            }

            if (!await EnsureParentAsync(record.DestinationFileSystem, record.DestinationPath, L10n.T(title))
                    .ConfigureAwait(true))
            {
                return false;
            }
        }

        var errors = new List<string>();
        foreach (var record in pending)
        {
            try
            {
                await MoveEntryAsync(record.SourceFileSystem, record.SourcePath,
                    record.DestinationFileSystem, record.DestinationPath, CancellationToken.None).ConfigureAwait(true);
                record.DestinationEntry = await TryGetEntryAsync(record.DestinationFileSystem, record.DestinationPath)
                    .ConfigureAwait(true) ?? record.SourceEntry;
                record.Applied = true;
            }
            catch (Exception ex)
            {
                errors.Add($"{record.SourcePath}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
            return true;

        await _services.Dialogs.ShowErrorAsync(L10n.T("重做移动时发生错误"),
            string.Join(Environment.NewLine, errors.Take(8))).ConfigureAwait(true);
        return false;
    }

    private Task<bool> UndoRenamesAsync(List<RenameRecord> records)
        => ExecuteRenameSetAsync(records, undo: true);

    private Task<bool> RedoRenamesAsync(List<RenameRecord> records)
        => ExecuteRenameSetAsync(records, undo: false);

    private async Task<bool> ExecuteRenameSetAsync(List<RenameRecord> records, bool undo)
    {
        var title = L10n.T(undo ? "无法撤销重命名" : "无法重做重命名");
        var active = records.Where(record => record.Applied == undo).ToList();
        if (active.Count == 0)
            return true;

        var operations = active
            .Select(record => new RenameOperation(
                record,
                undo ? record.NewPath : record.OldPath,
                undo ? record.OldPath : record.NewPath))
            .ToList();

        var groups = operations.GroupBy(operation => operation.Record.FileSystem).ToList();
        foreach (var group in groups)
        {
            var fileSystem = group.Key;
            var comparer = fileSystem.IsCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var sourcePaths = group.Select(operation => fileSystem.NormalizePath(operation.Source)).ToHashSet(comparer);
            var targets = new HashSet<string>(comparer);

            foreach (var operation in group)
            {
                if (!await MatchesAsync(fileSystem, operation.Source, operation.Record.Snapshot).ConfigureAwait(true))
                {
                    return await ConflictAsync(title,
                        L10n.Format("重命名后的项目已被修改或删除：{0}", operation.Source)).ConfigureAwait(true);
                }

                if (!targets.Add(operation.Target))
                {
                    return await ConflictAsync(title,
                        L10n.Format("目标位置已被占用：{0}", operation.Target)).ConfigureAwait(true);
                }

                if (!sourcePaths.Contains(operation.Target) &&
                    await fileSystem.ExistsAsync(operation.Target).ConfigureAwait(true))
                {
                    return await ConflictAsync(title,
                        L10n.Format("目标位置已被占用：{0}", operation.Target)).ConfigureAwait(true);
                }

                if (!await EnsureParentAsync(fileSystem, operation.Target, title).ConfigureAwait(true))
                    return false;
            }
        }

        var staged = new List<RenameOperation>();
        foreach (var operation in operations)
        {
            var fileSystem = operation.Record.FileSystem;
            var parent = fileSystem.GetParent(operation.Source);
            var temp = fileSystem.Combine(parent, ".ordoa-undo-" + Guid.NewGuid().ToString("N"));
            operation.Temp = fileSystem.NormalizePath(temp);
            try
            {
                await fileSystem.MoveAsync(operation.Source, operation.Temp, CancellationToken.None)
                    .ConfigureAwait(true);
                staged.Add(operation);
            }
            catch (Exception ex)
            {
                foreach (var previous in staged.AsEnumerable().Reverse())
                {
                    try
                    {
                        await previous.Record.FileSystem
                            .MoveAsync(previous.Temp, previous.Source, CancellationToken.None).ConfigureAwait(true);
                    }
                    catch (Exception rollback)
                    {
                        Log.Warn("撤销重命名回滚失败", rollback);
                    }
                }

                await _services.Dialogs.ShowErrorAsync(
                    L10n.T(undo ? "撤销重命名时发生错误" : "重做重命名时发生错误"), ex.Message).ConfigureAwait(true);
                return false;
            }
        }

        var errors = new List<string>();
        foreach (var operation in operations)
        {
            try
            {
                await operation.Record.FileSystem
                    .MoveAsync(operation.Temp, operation.Target, CancellationToken.None).ConfigureAwait(true);
                operation.Record.Snapshot =
                    await TryGetEntryAsync(operation.Record.FileSystem, operation.Target).ConfigureAwait(true) ??
                    operation.Record.Snapshot;
                operation.Record.Applied = !undo;
            }
            catch (Exception ex)
            {
                errors.Add($"{operation.Target}: {ex.Message}");
                try
                {
                    await operation.Record.FileSystem
                        .MoveAsync(operation.Temp, operation.Source, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception rollback)
                {
                    errors.Add($"{operation.Source}: {rollback.Message}");
                }
            }
        }

        if (errors.Count == 0)
            return true;

        await _services.Dialogs.ShowErrorAsync(
            L10n.T(undo ? "撤销重命名时发生错误" : "重做重命名时发生错误"),
            string.Join(Environment.NewLine, errors.Take(8))).ConfigureAwait(true);
        return false;
    }

    private async Task<bool> UndoNewAsync(NewRecord record)
    {
        const string title = "无法撤销新建";
        if (!await MatchesAsync(record.FileSystem, record.Path, record.Snapshot, requireEmptyDirectory: true)
                .ConfigureAwait(true))
        {
            return await ConflictAsync(L10n.T(title),
                L10n.Format("新建的项目已被修改或删除：{0}", record.Path)).ConfigureAwait(true);
        }

        try
        {
            await record.FileSystem.DeleteAsync(record.Path, true, CancellationToken.None).ConfigureAwait(true);
            record.Applied = false;
            return true;
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ShowErrorAsync(L10n.T("撤销新建时发生错误"), ex.Message).ConfigureAwait(true);
            return false;
        }
    }

    private async Task<bool> RedoNewAsync(NewRecord record)
    {
        const string title = "无法重做新建";
        if (!await EnsureFreeAsync(record.FileSystem, record.Path, L10n.T(title)).ConfigureAwait(true))
            return false;

        if (!await EnsureParentAsync(record.FileSystem, record.Path, L10n.T(title)).ConfigureAwait(true))
            return false;

        try
        {
            if (record.Snapshot.IsDirectory)
                await record.FileSystem.CreateDirectoryAsync(record.Path, CancellationToken.None).ConfigureAwait(true);
            else
                await using (await record.FileSystem.OpenWriteAsync(record.Path, false, null, CancellationToken.None)
                                 .ConfigureAwait(true))
                {
                }

            record.Snapshot = await TryGetEntryAsync(record.FileSystem, record.Path).ConfigureAwait(true)
                              ?? record.Snapshot;
            record.Applied = true;
            return true;
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ShowErrorAsync(L10n.T("重做新建时发生错误"), ex.Message).ConfigureAwait(true);
            return false;
        }
    }

    private async Task<bool> UndoTrashAsync(List<TrashRecord> records)
    {
        const string title = "无法撤销移到回收站";
        var active = records.Where(record => record.Entry is not null).ToList();
        foreach (var record in active)
        {
            var entry = record.Entry!;
            if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
            {
                return await ConflictAsync(L10n.T(title),
                    L10n.Format("原位置已被占用：{0}", entry.OriginalPath)).ConfigureAwait(true);
            }

            if (!File.Exists(entry.TrashPath) && !Directory.Exists(entry.TrashPath))
            {
                return await ConflictAsync(L10n.T(title),
                    L10n.Format("回收站中的项目已不存在：{0}", entry.TrashPath)).ConfigureAwait(true);
            }
        }

        var errors = new List<string>();
        foreach (var record in active)
        {
            try
            {
                await _services.Trash.RestoreAsync(record.Entry!, CancellationToken.None).ConfigureAwait(true);
                record.Entry = null;
            }
            catch (Exception ex)
            {
                errors.Add($"{record.Source.Path}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
            return true;

        await _services.Dialogs.ShowErrorAsync(L10n.T("撤销回收站操作时发生错误"),
            string.Join(Environment.NewLine, errors.Take(8))).ConfigureAwait(true);
        return false;
    }

    private async Task<bool> RedoTrashAsync(List<TrashRecord> records)
    {
        const string title = "无法重做移到回收站";
        var pending = records.Where(record => record.Entry is null).ToList();
        foreach (var record in pending)
        {
            if (!await MatchesAsync(record.Source.FileSystem, record.Source.Path, record.Source.Entry)
                    .ConfigureAwait(true))
            {
                return await ConflictAsync(L10n.T(title),
                    L10n.Format("项目已被修改或删除：{0}", record.Source.Path)).ConfigureAwait(true);
            }

            if (!await EnsureParentAsync(record.Source.FileSystem, record.Source.Path, L10n.T(title))
                    .ConfigureAwait(true))
            {
                return false;
            }
        }

        var errors = new List<string>();
        foreach (var record in pending)
        {
            try
            {
                record.Entry = await _services.Trash.MoveToTrashAsync(record.Source.Path, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                errors.Add($"{record.Source.Path}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
            return true;

        await _services.Dialogs.ShowErrorAsync(L10n.T("重做回收站操作时发生错误"),
            string.Join(Environment.NewLine, errors.Take(8))).ConfigureAwait(true);
        return false;
    }

    private static async Task CopyEntryAsync(IFileSystem sourceFileSystem, FileEntry entry, string sourcePath,
        IFileSystem destinationFileSystem, string destinationPath, CancellationToken cancellationToken)
    {
        if (entry.IsSymlink && sourceFileSystem.IsLocal && destinationFileSystem.IsLocal &&
            !string.IsNullOrEmpty(entry.LinkTarget))
        {
            try
            {
                if (entry.IsDirectory)
                    Directory.CreateSymbolicLink(destinationPath, entry.LinkTarget);
                else
                    File.CreateSymbolicLink(destinationPath, entry.LinkTarget);

                return;
            }
            catch
            {
                // Fall back to copying the file contents.
            }
        }

        if (entry.IsDirectory)
        {
            await destinationFileSystem.CreateDirectoryAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            var children = await sourceFileSystem.ListAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            foreach (var child in children)
            {
                var childSource = sourceFileSystem.Combine(sourcePath, child.Name);
                var childDestination = destinationFileSystem.Combine(destinationPath, child.Name);
                await CopyEntryAsync(sourceFileSystem, child, childSource, destinationFileSystem, childDestination,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await destinationFileSystem
                .CopyFileAsync(sourcePath, destinationPath, false, null, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (entry.Modified is { } modified)
                await destinationFileSystem.SetModifiedAsync(destinationPath, modified, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch { /* best effort */ }

        try
        {
            if (entry.UnixMode is { } mode &&
                destinationFileSystem.Capabilities.Has(FileSystemCapabilities.UnixPermissions))
            {
                await destinationFileSystem.SetUnixModeAsync(destinationPath, mode, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch { /* best effort */ }
    }

    private static async Task MoveEntryAsync(IFileSystem sourceFileSystem, string sourcePath,
        IFileSystem destinationFileSystem, string destinationPath, CancellationToken cancellationToken)
    {
        if (string.Equals(sourceFileSystem.Id, destinationFileSystem.Id, StringComparison.Ordinal))
        {
            await sourceFileSystem.MoveAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        var entry = await sourceFileSystem.GetEntryAsync(sourcePath, cancellationToken).ConfigureAwait(false)
                    ?? throw new FileNotFoundException(L10n.T("项目已不存在"), sourcePath);
        await CopyEntryAsync(sourceFileSystem, entry, sourcePath, destinationFileSystem, destinationPath,
            cancellationToken).ConfigureAwait(false);
        await sourceFileSystem.DeleteAsync(sourcePath, entry.IsDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> MatchesAsync(IFileSystem fileSystem, string path, FileEntry? snapshot,
        bool requireEmptyDirectory = false)
    {
        try
        {
            var current = await fileSystem.GetEntryAsync(path).ConfigureAwait(false);
            if (current is null)
                return false;

            if (snapshot is null)
                return true;

            if (current.IsDirectory != snapshot.IsDirectory)
                return false;

            if (!current.IsDirectory && current.Size != snapshot.Size)
                return false;

            if (current.IsDirectory && requireEmptyDirectory)
            {
                var children = await fileSystem.ListAsync(path).ConfigureAwait(false);
                if (children.Count > 0)
                    return false;
            }

            if (fileSystem.IsLocal &&
                snapshot.Modified is { } expected &&
                current.Modified is { } actual &&
                Math.Abs((actual - expected).TotalSeconds) > 2)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> EnsureFreeAsync(IFileSystem fileSystem, string path, string title)
    {
        try
        {
            if (await fileSystem.ExistsAsync(path).ConfigureAwait(true))
            {
                return await ConflictAsync(title,
                    L10n.Format("目标位置已被占用：{0}", path)).ConfigureAwait(true);
            }

            return true;
        }
        catch (Exception ex)
        {
            return await ConflictAsync(title, ex.Message).ConfigureAwait(true);
        }
    }

    private async Task<bool> EnsureParentAsync(IFileSystem fileSystem, string path, string title)
    {
        try
        {
            var parent = fileSystem.GetParent(path);
            var entry = await fileSystem.GetEntryAsync(parent).ConfigureAwait(true);
            if (entry is not { IsDirectory: true })
                return await ConflictAsync(title, L10n.Format("目标文件夹不存在：{0}", parent)).ConfigureAwait(true);

            return true;
        }
        catch (Exception ex)
        {
            return await ConflictAsync(title, ex.Message).ConfigureAwait(true);
        }
    }

    private async Task<bool> ConflictAsync(string title, string message)
    {
        await _services.Dialogs.ShowErrorAsync(title, message).ConfigureAwait(true);
        return false;
    }

    private static async Task<FileEntry?> TryGetEntryAsync(IFileSystem fileSystem, string path)
    {
        try
        {
            return await fileSystem.GetEntryAsync(path).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task RefreshAsync()
    {
        foreach (var window in _services.Windows.Windows)
        {
            if (window.DataContext is MainViewModel viewModel)
            {
                try
                {
                    await viewModel.RefreshAllPanesAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Log.Warn("撤销后刷新文件列表失败", ex);
                }
            }
        }
    }

    private void Notify(string title, string message, bool error)
    {
        if (_services.Windows.ActiveWindow?.DataContext is MainViewModel viewModel)
            viewModel.AddToast(title, message, error ? ToastKind.Error : ToastKind.Success);
    }

    private sealed class CopyRecord(
        IFileSystem sourceFileSystem,
        FileEntry sourceEntry,
        string sourcePath,
        IFileSystem destinationFileSystem,
        string destinationPath,
        FileEntry? destinationEntry)
    {
        public IFileSystem SourceFileSystem { get; } = sourceFileSystem;
        public FileEntry SourceEntry { get; } = sourceEntry;
        public string SourcePath { get; } = sourcePath;
        public IFileSystem DestinationFileSystem { get; } = destinationFileSystem;
        public string DestinationPath { get; } = destinationPath;
        public FileEntry? DestinationEntry { get; set; } = destinationEntry;
        public bool Applied { get; set; } = true;
    }

    private sealed class MoveRecord(
        IFileSystem sourceFileSystem,
        FileEntry sourceEntry,
        string sourcePath,
        IFileSystem destinationFileSystem,
        string destinationPath,
        FileEntry? destinationEntry)
    {
        public IFileSystem SourceFileSystem { get; } = sourceFileSystem;
        public FileEntry SourceEntry { get; set; } = sourceEntry;
        public string SourcePath { get; } = sourcePath;
        public IFileSystem DestinationFileSystem { get; } = destinationFileSystem;
        public string DestinationPath { get; } = destinationPath;
        public FileEntry? DestinationEntry { get; set; } = destinationEntry;
        public bool Applied { get; set; } = true;
    }

    private sealed class TrashRecord(OperationSource source, TrashEntry entry)
    {
        public OperationSource Source { get; } = source;
        public TrashEntry? Entry { get; set; } = entry;
    }

    private sealed class RenameOperation(RenameRecord record, string source, string target)
    {
        public RenameRecord Record { get; } = record;
        public string Source { get; } = source;
        public string Target { get; } = target;
        public string Temp { get; set; } = string.Empty;
    }

    private sealed class RenameRecord(
        IFileSystem fileSystem,
        string oldPath,
        string newPath,
        FileEntry snapshot)
    {
        public IFileSystem FileSystem { get; } = fileSystem;
        public string OldPath { get; } = oldPath;
        public string NewPath { get; } = newPath;
        public FileEntry Snapshot { get; set; } = snapshot;
        public bool Applied { get; set; } = true;
    }

    private sealed class NewRecord(IFileSystem fileSystem, string path, FileEntry snapshot)
    {
        public IFileSystem FileSystem { get; } = fileSystem;
        public string Path { get; } = path;
        public FileEntry Snapshot { get; set; } = snapshot;
        public bool Applied { get; set; } = true;
    }
}
