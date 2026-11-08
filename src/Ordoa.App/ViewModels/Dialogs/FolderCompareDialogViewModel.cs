using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.App.Services;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;
using Ordoa.Core.Localization;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed class FolderCompareRowViewModel
{
    private static readonly IBrush SameBrush = new SolidColorBrush(Color.Parse("#87918B"));
    private static readonly IBrush LeftBrush = new SolidColorBrush(Color.Parse("#C9862B"));
    private static readonly IBrush RightBrush = new SolidColorBrush(Color.Parse("#3B7EDB"));
    private static readonly IBrush DifferentBrush = new SolidColorBrush(Color.Parse("#D9534F"));

    public FolderCompareRowViewModel(FolderCompareItem item)
    {
        Item = item;
    }

    public FolderCompareItem Item { get; }
    public string RelativePath => Item.RelativePath;

    public string StateText => Item.State switch
    {
        FolderCompareState.Same => L10n.T("相同"),
        FolderCompareState.OnlyLeft => L10n.T("仅左侧"),
        FolderCompareState.OnlyRight => L10n.T("仅右侧"),
        FolderCompareState.Different => L10n.T("内容不同"),
        FolderCompareState.LeftNewer => L10n.T("左侧较新"),
        FolderCompareState.RightNewer => L10n.T("右侧较新"),
        _ => L10n.T("类型不同"),
    };

    public IBrush StateBrush => Item.State switch
    {
        FolderCompareState.OnlyLeft => LeftBrush,
        FolderCompareState.OnlyRight => RightBrush,
        FolderCompareState.Same => SameBrush,
        _ => DifferentBrush,
    };

    public string LeftInfo => Describe(Item.LeftEntry);
    public string RightInfo => Describe(Item.RightEntry);
    public bool CanCopyRight => Item.CanCopyRight;
    public bool CanCopyLeft => Item.CanCopyLeft;

    private static string Describe(FileEntry? entry)
    {
        if (entry is null)
            return "—";
        if (entry.IsDirectory)
            return L10n.T("文件夹");
        return $"{Humanize.Size(entry.Size)} · {Humanize.Date(entry.Modified)}";
    }
}

public sealed partial class FolderCompareDialogViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly IFileSystem _leftFileSystem;
    private readonly IFileSystem _rightFileSystem;
    private readonly string _leftRoot;
    private readonly string _rightRoot;
    private readonly FolderCompareService _compare = new();
    private readonly List<FolderCompareRowViewModel> _allRows = [];
    private CancellationTokenSource? _cts;
    private bool _applyingFilter;

    public FolderCompareDialogViewModel(AppServices services, IFileSystem leftFileSystem, string leftRoot,
        IFileSystem rightFileSystem, string rightRoot)
    {
        _services = services;
        _leftFileSystem = leftFileSystem;
        _rightFileSystem = rightFileSystem;
        _leftRoot = leftFileSystem.NormalizePath(leftRoot);
        _rightRoot = rightFileSystem.NormalizePath(rightRoot);
        LeftPathText = leftFileSystem.GetDisplayPath(_leftRoot);
        RightPathText = rightFileSystem.GetDisplayPath(_rightRoot);
    }

    public ObservableCollection<FolderCompareRowViewModel> Rows { get; } = [];
    public string LeftPathText { get; }
    public string RightPathText { get; }

    [ObservableProperty]
    private bool _includeHidden;

    [ObservableProperty]
    private bool _compareContents;

    [ObservableProperty]
    private bool _showOnlyDifferences = true;

    [ObservableProperty]
    private bool _dryRun;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _hasScanned;

    [ObservableProperty]
    private string _status = L10n.T("准备比较…");

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private int _differenceCount;

    [ObservableProperty]
    private int _syncRightCount;

    [ObservableProperty]
    private int _syncLeftCount;

    public bool HasRows => Rows.Count > 0;
    public bool CanSyncRight => SyncRightCount > 0 && !IsScanning;
    public bool CanSyncLeft => SyncLeftCount > 0 && !IsScanning;

    partial void OnShowOnlyDifferencesChanged(bool value) => ApplyFilter();

    partial void OnIncludeHiddenChanged(bool value) => _ = ScanAsync();

    partial void OnCompareContentsChanged(bool value) => _ = ScanAsync();

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSyncRight));
        OnPropertyChanged(nameof(CanSyncLeft));
    }

    public async Task ScanAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsScanning = true;
        HasScanned = false;
        Rows.Clear();
        _allRows.Clear();
        DifferenceCount = 0;
        SyncLeftCount = 0;
        SyncRightCount = 0;
        Status = L10n.T("正在比较…");
        ProgressText = string.Empty;

        var progress = new Progress<FolderCompareProgress>(value =>
        {
            ProgressText = L10n.Format("已扫描 {0} 项 · 发现 {1} 个差异", value.Scanned, value.Differences);
        });

        try
        {
            var items = await _compare.CompareAsync(
                _leftFileSystem, _leftRoot, _rightFileSystem, _rightRoot,
                new FolderCompareOptions(IncludeHidden, CompareContents),
                progress, token).ConfigureAwait(true);

            foreach (var item in items)
                _allRows.Add(new FolderCompareRowViewModel(item));

            HasScanned = true;
            DifferenceCount = _allRows.Count(row => row.Item.State != FolderCompareState.Same);
            SyncRightCount = CountSyncRight();
            SyncLeftCount = CountSyncLeft();
            ApplyFilter();
            Status = L10n.Format("比较完成：{0} 项，{1} 个差异", _allRows.Count, DifferenceCount);
        }
        catch (OperationCanceledException)
        {
            Status = L10n.T("已取消");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsScanning = false;
            ProgressText = string.Empty;
            OnPropertyChanged(nameof(CanSyncRight));
            OnPropertyChanged(nameof(CanSyncLeft));
        }
    }

    public async Task SyncAsync(bool toRight)
    {
        if (IsScanning)
            return;

        var candidates = toRight
            ? _allRows.Where(row => IsSyncableRight(row.Item)).ToList()
            : _allRows.Where(row => IsSyncableLeft(row.Item)).ToList();
        if (candidates.Count == 0)
        {
            Status = L10n.T("没有需要同步的项目");
            return;
        }

        if (DryRun)
        {
            Status = L10n.Format("试运行：将向{0}同步 {1} 个项目",
                toRight ? L10n.T("右侧") : L10n.T("左侧"), candidates.Count);
            return;
        }

        var sourceFileSystem = toRight ? _leftFileSystem : _rightFileSystem;
        var destinationFileSystem = toRight ? _rightFileSystem : _leftFileSystem;
        var destinationRoot = toRight ? _rightRoot : _leftRoot;
        var copied = 0;

        try
        {
            IsScanning = true;
            Status = L10n.T("正在准备同步…");

            foreach (var row in candidates.OrderBy(row => row.RelativePath, StringComparer.Ordinal))
            {
                if (row.Item.State == FolderCompareState.OnlyLeft && row.Item.LeftIsDirectory && toRight)
                {
                    await EnsureDirectoryAsync(destinationFileSystem, destinationRoot, row.RelativePath, _cts?.Token ?? default)
                        .ConfigureAwait(true);
                }
                else if (row.Item.State == FolderCompareState.OnlyRight && row.Item.RightIsDirectory && !toRight)
                {
                    await EnsureDirectoryAsync(destinationFileSystem, destinationRoot, row.RelativePath, _cts?.Token ?? default)
                        .ConfigureAwait(true);
                }
            }

            var files = candidates.Where(row => toRight
                    ? row.Item.LeftEntry is { IsDirectory: false }
                    : row.Item.RightEntry is { IsDirectory: false })
                .ToList();

            foreach (var group in files.GroupBy(row => DestinationDirectory(destinationFileSystem, destinationRoot, row.RelativePath)))
            {
                var sources = group
                    .Select(row => toRight ? row.Item.LeftEntry : row.Item.RightEntry)
                    .Where(entry => entry is not null)
                    .Select(entry => new OperationSource(sourceFileSystem, entry!))
                    .ToList();
                if (sources.Count == 0)
                    continue;

                await EnsureDirectoryAsync(destinationFileSystem, destinationRoot,
                    RelativeDirectory(group.Key, destinationFileSystem, destinationRoot), _cts?.Token ?? default)
                    .ConfigureAwait(true);
                _services.Operations.Copy(sources, destinationFileSystem, group.Key, move: false,
                    ConflictChoice.ReplaceAll);
                copied += sources.Count;
            }

            Status = copied == 0
                ? L10n.T("只创建了目录，没有需要复制的文件")
                : L10n.Format("已加入 {0} 个复制任务", copied);
        }
        catch (OperationCanceledException)
        {
            Status = L10n.T("已取消");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    public async Task SyncRowAsync(FolderCompareRowViewModel row, bool toRight)
    {
        if (IsScanning)
            return;

        var entry = toRight ? row.Item.LeftEntry : row.Item.RightEntry;
        if (entry is null || entry.IsDirectory)
            return;

        var sourceFileSystem = toRight ? _leftFileSystem : _rightFileSystem;
        var destinationFileSystem = toRight ? _rightFileSystem : _leftFileSystem;
        var destinationRoot = toRight ? _rightRoot : _leftRoot;
        var directory = DestinationDirectory(destinationFileSystem, destinationRoot, row.RelativePath);

        if (DryRun)
        {
            Status = L10n.Format("试运行：将复制 {0}", row.RelativePath);
            return;
        }

        try
        {
            await EnsureDirectoryAsync(destinationFileSystem, destinationRoot,
                RelativeDirectory(directory, destinationFileSystem, destinationRoot), _cts?.Token ?? default)
                .ConfigureAwait(true);
            _services.Operations.Copy([new OperationSource(sourceFileSystem, entry)],
                destinationFileSystem, directory, move: false, ConflictChoice.ReplaceAll);
            Status = L10n.Format("已加入复制任务：{0}", row.RelativePath);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        Status = L10n.T("已取消");
    }

    private bool IsSyncableRight(FolderCompareItem item) => item.State switch
    {
        FolderCompareState.OnlyLeft => true,
        FolderCompareState.Different => item.LeftEntry is { IsDirectory: false },
        FolderCompareState.LeftNewer => item.LeftEntry is { IsDirectory: false },
        _ => false,
    };

    private bool IsSyncableLeft(FolderCompareItem item) => item.State switch
    {
        FolderCompareState.OnlyRight => true,
        FolderCompareState.Different => item.RightEntry is { IsDirectory: false },
        FolderCompareState.RightNewer => item.RightEntry is { IsDirectory: false },
        _ => false,
    };

    private int CountSyncRight() => _allRows.Count(row => IsSyncableRight(row.Item));
    private int CountSyncLeft() => _allRows.Count(row => IsSyncableLeft(row.Item));

    private void ApplyFilter()
    {
        if (_applyingFilter)
            return;

        _applyingFilter = true;
        try
        {
            Rows.Clear();
            foreach (var row in _allRows)
            {
                if (!ShowOnlyDifferences || row.Item.State != FolderCompareState.Same)
                    Rows.Add(row);
            }
        }
        finally
        {
            _applyingFilter = false;
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CanSyncRight));
        OnPropertyChanged(nameof(CanSyncLeft));
    }

    private static string DestinationDirectory(IFileSystem fileSystem, string root, string relativePath)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
            current = fileSystem.Combine(current, parts[i]);
        return current;
    }

    private static string RelativeDirectory(string directory, IFileSystem fileSystem, string root)
    {
        var normalizedRoot = fileSystem.NormalizePath(root).TrimEnd('/', '\\');
        var normalized = fileSystem.NormalizePath(directory).TrimEnd('/', '\\');
        if (normalized.Length <= normalizedRoot.Length ||
            !normalized.StartsWith(normalizedRoot, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return normalized[(normalizedRoot.Length + 1)..].Replace('\\', '/');
    }

    private static async Task EnsureDirectoryAsync(IFileSystem fileSystem, string root, string relativeDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory))
            return;

        var current = root;
        foreach (var part in relativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = fileSystem.Combine(current, part);
            if (await fileSystem.ExistsAsync(current, cancellationToken).ConfigureAwait(false))
                continue;
            try
            {
                await fileSystem.CreateDirectoryAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Another item in the batch may have created it first.
                if (!await fileSystem.ExistsAsync(current, cancellationToken).ConfigureAwait(false))
                    throw;
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}
