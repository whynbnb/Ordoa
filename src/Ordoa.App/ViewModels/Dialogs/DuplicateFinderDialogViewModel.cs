using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.App.Services;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed partial class DuplicateFileItem : ObservableObject
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public string SizeText => Humanize.Size(Size);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class DuplicateGroupViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _suppressSelectionChanged;

    public DuplicateGroupViewModel(DuplicateGroup group, Action selectionChanged)
    {
        Size = group.Size;
        _selectionChanged = selectionChanged;
        Name = System.IO.Path.GetFileName(group.Paths.FirstOrDefault() ?? string.Empty);
        if (string.IsNullOrEmpty(Name))
            Name = L10n.T("重复文件");

        var paths = group.Paths
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        for (var index = 0; index < paths.Count; index++)
        {
            var item = new DuplicateFileItem
            {
                Path = paths[index],
                Name = System.IO.Path.GetFileName(paths[index]),
                Size = group.Size,
                IsSelected = index > 0,
            };
            item.PropertyChanged += OnFilePropertyChanged;
            Files.Add(item);
        }
    }

    public string Name { get; }
    public long Size { get; }
    public ObservableCollection<DuplicateFileItem> Files { get; } = [];
    public string SizeText => Humanize.Size(Size);
    public int Count => Files.Count;
    public long WastedBytes => Count <= 1 ? 0 : Size * (Count - 1);
    public string WastedText => Humanize.Size(WastedBytes);
    public string Summary => L10n.Format("{0} 个副本 · 可释放 {1}", Count, WastedText);

    public IEnumerable<DuplicateFileItem> SelectedFiles
        => Files.Where(file => file.IsSelected);

    public void SelectDuplicates()
    {
        _suppressSelectionChanged = true;
        try
        {
            for (var index = 0; index < Files.Count; index++)
                Files[index].IsSelected = index > 0;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        _selectionChanged();
    }

    public void ClearSelection()
    {
        _suppressSelectionChanged = true;
        try
        {
            foreach (var file in Files)
                file.IsSelected = false;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        _selectionChanged();
    }

    public void RemoveFiles(IReadOnlySet<string> paths)
    {
        for (var index = Files.Count - 1; index >= 0; index--)
        {
            if (!paths.Contains(Files[index].Path))
                continue;

            Files[index].PropertyChanged -= OnFilePropertyChanged;
            Files.RemoveAt(index);
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(WastedBytes));
        OnPropertyChanged(nameof(WastedText));
        OnPropertyChanged(nameof(Summary));
    }

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressSelectionChanged && e.PropertyName == nameof(DuplicateFileItem.IsSelected))
            _selectionChanged();
    }
}

public sealed partial class DuplicateFinderDialogViewModel : ObservableObject, IDisposable
{
    private const int MaxDisplayedGroups = 500;

    private readonly string _rootPath;
    private readonly IDialogService _dialogs;
    private readonly TrashService _trash;
    private readonly IProgress<DuplicateScanProgress> _progress;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _deleteCts;
    private DuplicateScanResult? _result;
    private bool _disposed;
    private bool _scanCompleted;
    private bool _suppressSelectionUpdates;

    public DuplicateFinderDialogViewModel(string rootPath, IDialogService dialogs, TrashService trash)
    {
        _rootPath = rootPath;
        _dialogs = dialogs;
        _trash = trash;
        _progress = new Progress<DuplicateScanProgress>(OnProgress);
        StatusText = L10n.T("准备查找重复文件…");
    }

    public string RootPath => _rootPath;
    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];
    public bool HasGroups => Groups.Count > 0;
    public bool CanUseTrash => _trash.IsSupported;

    [ObservableProperty]
    private decimal _minSizeKb;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isDeleting;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _counterText = string.Empty;

    [ObservableProperty]
    private string _scanSummary = string.Empty;

    [ObservableProperty]
    private string _groupsNote = string.Empty;

    [ObservableProperty]
    private string _selectedSummary = string.Empty;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private long _selectedBytes;

    public bool CanDelete => !IsDeleting && CanUseTrash && SelectedCount > 0;
    public bool CanScan => !IsScanning && !IsDeleting;
    public bool ShowEmptyState => IsEmpty && !IsScanning;

    partial void OnIsDeletingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanScan));
    }

    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(CanDelete));

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(CanScan));
    }

    public async Task StartAsync()
    {
        if (_result is not null)
            return;
        await ScanAsync().ConfigureAwait(true);
    }

    public Task RescanAsync()
    {
        if (IsScanning)
        {
            Cancel();
            return Task.CompletedTask;
        }

        return ScanAsync();
    }

    public async Task ScanAsync()
    {
        if (IsScanning || IsDeleting)
            return;

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        _result = null;
        _scanCompleted = false;
        IsScanning = true;
        IsEmpty = true;
        Groups.Clear();
        UpdateSelectionSummary();
        ScanSummary = string.Empty;
        GroupsNote = string.Empty;
        CounterText = string.Empty;
        StatusText = L10n.T("正在扫描…");
        OnPropertyChanged(nameof(HasGroups));

        try
        {
            var minimumBytes = Math.Max(0, (long)(MinSizeKb * 1024));
            var result = await DuplicateScanner.ScanAsync(_rootPath, minimumBytes, _progress, cts.Token)
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;

            _scanCompleted = true;
            _result = result;
            BuildGroups(result);
            ScanSummary = L10n.Format("共扫描 {0} 个文件 · {1} 组重复 · 可释放 {2}",
                result.FilesScanned, result.Groups.Count, Humanize.Size(result.WastedBytes));
            StatusText = L10n.Format("扫描完成，用时 {0:0.0} 秒", result.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            StatusText = L10n.T("扫描已取消");
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            IsEmpty = true;
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
                _scanCts = null;
            cts.Dispose();
            IsScanning = false;
        }
    }

    public void Cancel()
    {
        try { _scanCts?.Cancel(); } catch { /* ignore */ }
        try { _deleteCts?.Cancel(); } catch { /* ignore */ }
    }

    public void SelectAllDuplicates()
    {
        _suppressSelectionUpdates = true;
        try
        {
            foreach (var group in Groups)
                group.SelectDuplicates();
        }
        finally
        {
            _suppressSelectionUpdates = false;
        }

        UpdateSelectionSummary();
    }

    public void ClearSelection()
    {
        _suppressSelectionUpdates = true;
        try
        {
            foreach (var group in Groups)
                group.ClearSelection();
        }
        finally
        {
            _suppressSelectionUpdates = false;
        }

        UpdateSelectionSummary();
    }

    public async Task DeleteSelectedAsync()
    {
        if (!CanDelete || IsDeleting)
            return;

        var selected = Groups
            .SelectMany(group => group.SelectedFiles)
            .Select(file => file.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (selected.Count == 0)
            return;

        var bytes = Groups
            .SelectMany(group => group.SelectedFiles)
            .Select(file => file.Size)
            .Sum();
        var confirmed = await _dialogs.ConfirmAsync(
            L10n.T("移到回收站？"),
            L10n.Format("将选中的 {0} 个重复文件移到回收站？可释放 {1}。",
                selected.Count, Humanize.Size(bytes)),
            L10n.T("移到回收站"), destructive: true).ConfigureAwait(true);
        if (!confirmed)
            return;

        IsDeleting = true;
        StatusText = L10n.T("正在移到回收站…");
        var moved = new List<string>();
        var failed = 0;
        var cts = new CancellationTokenSource();
        _deleteCts = cts;
        try
        {
            foreach (var path in selected)
            {
                cts.Token.ThrowIfCancellationRequested();
                try
                {
                    await _trash.MoveToTrashAsync(path, cts.Token).ConfigureAwait(true);
                    moved.Add(path);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Warn($"移动重复文件到回收站失败: {path}", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = L10n.T("操作已取消");
        }
        finally
        {
            if (ReferenceEquals(_deleteCts, cts))
                _deleteCts = null;
            cts.Dispose();
            IsDeleting = false;
        }

        if (moved.Count > 0)
        {
            if (_disposed)
                return;

            var movedSet = moved.ToHashSet(StringComparer.Ordinal);
            for (var index = Groups.Count - 1; index >= 0; index--)
            {
                var group = Groups[index];
                group.RemoveFiles(movedSet);
                if (group.Count < 2)
                    Groups.RemoveAt(index);
            }

            OnPropertyChanged(nameof(HasGroups));
            UpdateSelectionSummary();
            IsEmpty = Groups.Count == 0;
            if (Groups.Count < MaxDisplayedGroups)
                GroupsNote = string.Empty;
            StatusText = failed == 0
                ? L10n.Format("已移除 {0} 个文件", moved.Count)
                : L10n.Format("已移除 {0} 个文件，{1} 个失败", moved.Count, failed);
        }
        else if (failed > 0)
        {
            StatusText = L10n.Format("{0} 个文件移动失败", failed);
        }
    }

    private void BuildGroups(DuplicateScanResult result)
    {
        Groups.Clear();
        foreach (var group in result.Groups.Take(MaxDisplayedGroups))
        {
            var vm = new DuplicateGroupViewModel(group, UpdateSelectionSummary);
            Groups.Add(vm);
        }

        GroupsNote = result.Groups.Count > MaxDisplayedGroups
            ? L10n.Format("结果较多，仅显示占用最大的前 {0} 组。", MaxDisplayedGroups)
            : string.Empty;
        OnPropertyChanged(nameof(HasGroups));
        IsEmpty = Groups.Count == 0;
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        if (_suppressSelectionUpdates)
            return;

        var selected = Groups.SelectMany(group => group.SelectedFiles).ToList();
        SelectedCount = selected.Count;
        SelectedBytes = selected.Sum(file => file.Size);
        SelectedSummary = L10n.Format("已选择 {0} 个文件 · {1}",
            SelectedCount, Humanize.Size(SelectedBytes));
        OnPropertyChanged(nameof(CanDelete));
    }

    private void OnProgress(DuplicateScanProgress progress)
    {
        if (_disposed || !IsScanning || _scanCompleted)
            return;

        StatusText = progress.Stage switch
        {
            DuplicateScanStage.Collecting => L10n.T("正在统计文件大小…"),
            DuplicateScanStage.QuickHash => L10n.T("正在快速筛选候选文件…"),
            DuplicateScanStage.FullHash => L10n.T("正在校验文件内容…"),
            _ => L10n.T("扫描完成"),
        };
        CounterText = L10n.Format("已扫描 {0} 个文件 · 已哈希 {1} 次 · 找到 {2} 组",
            progress.FilesScanned, progress.FilesHashed, progress.GroupsFound);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        _deleteCts?.Dispose();
        _deleteCts = null;
    }
}
