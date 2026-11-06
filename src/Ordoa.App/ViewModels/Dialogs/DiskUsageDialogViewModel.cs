using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.ViewModels.Dialogs;

public sealed class DiskUsageItem
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required bool IsDirectory { get; init; }
    public bool IsAggregate { get; init; }
    public DiskUsageNode? Node { get; init; }
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string SizeText => Humanize.Size(Size);
    public string PercentText { get; set; } = "0%";
    public IBrush Brush { get; set; } = Brushes.Transparent;
    public bool CanOpen => IsDirectory && !IsAggregate && Node is not null;
}

public sealed class DiskUsageFileItem
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required long Size { get; init; }
    public string SizeText => Humanize.Size(Size);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

public sealed partial class DiskUsageDialogViewModel : ObservableObject, IDisposable
{
    private const int MaxTiles = 200;
    private const int MaxDirectFiles = 200;

    private readonly string _rootPath;
    private readonly IProgress<DiskUsageProgress> _progress;
    private readonly Stack<DiskUsageNode> _history = new();
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _viewCts;
    private DiskUsageResult? _result;
    private bool _disposed;
    private bool _scanCompleted;
    private int _loadVersion;

    public DiskUsageDialogViewModel(string rootPath)
    {
        _rootPath = rootPath;
        _progress = new Progress<DiskUsageProgress>(OnProgress);
        CurrentPath = rootPath;
        CurrentTitle = Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath));
        if (string.IsNullOrEmpty(CurrentTitle))
            CurrentTitle = rootPath;
        StatusText = L10n.T("准备扫描…");
    }

    public ObservableCollection<DiskUsageItem> Items { get; } = [];
    public ObservableCollection<DiskUsageFileItem> LargestFiles { get; } = [];

    public string RootPath => _rootPath;

    private IReadOnlyList<TreemapEntry> _treemapEntries = [];
    public IReadOnlyList<TreemapEntry> TreemapEntries
    {
        get => _treemapEntries;
        private set => SetProperty(ref _treemapEntries, value);
    }

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private string _currentTitle = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _scanSummary = string.Empty;

    [ObservableProperty]
    private bool _canGoUp;

    [ObservableProperty]
    private DiskUsageItem? _selectedItem;

    public bool CanOpenSelected => SelectedItem is { CanOpen: true };

    partial void OnSelectedItemChanged(DiskUsageItem? value)
        => OnPropertyChanged(nameof(CanOpenSelected));

    public async Task StartAsync()
    {
        if (_result is not null)
            return;
        await ScanAsync().ConfigureAwait(true);
    }

    public async Task RescanAsync()
    {
        if (IsScanning)
        {
            Cancel();
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        _result = null;
        _history.Clear();
        await ScanAsync().ConfigureAwait(true);
    }

    public async Task GoUpAsync()
    {
        if (_history.Count <= 1)
            return;
        _history.Pop();
        await LoadNodeAsync(_history.Peek()).ConfigureAwait(true);
    }

    public async Task OpenItemAsync(DiskUsageItem? item)
    {
        if (item is not { CanOpen: true, Node: { } node })
            return;
        _history.Push(node);
        await LoadNodeAsync(node).ConfigureAwait(true);
    }

    public async Task OpenSelectedAsync()
        => await OpenItemAsync(SelectedItem).ConfigureAwait(true);

    public Task OpenEntryAsync(TreemapEntry entry)
    {
        var item = Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, entry.Id, StringComparison.Ordinal));
        return OpenItemAsync(item);
    }

    public void Cancel()
    {
        try { _scanCts?.Cancel(); } catch { /* ignore */ }
        try { _viewCts?.Cancel(); } catch { /* ignore */ }
    }

    private async Task ScanAsync()
    {
        if (IsScanning)
            return;

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        _scanCompleted = false;
        IsScanning = true;
        IsEmpty = true;
        Items.Clear();
        LargestFiles.Clear();
        TreemapEntries = [];
        ScanSummary = string.Empty;
        StatusText = L10n.T("正在扫描…");

        try
        {
            var result = await DiskUsageScanner.ScanAsync(_rootPath, _progress, cts.Token)
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;

            _scanCompleted = true;
            _result = result;
            _history.Clear();
            _history.Push(result.Root);
            ScanSummary = L10n.Format("共 {0} 个文件 · {1} 个文件夹 · {2}",
                result.FileCount, result.DirectoryCount, Humanize.Size(result.TotalBytes));

            LargestFiles.Clear();
            foreach (var file in result.LargestFiles.Take(20))
            {
                LargestFiles.Add(new DiskUsageFileItem
                {
                    Name = file.Name,
                    Path = file.Path,
                    Size = file.Size,
                });
            }

            await LoadNodeAsync(result.Root).ConfigureAwait(true);
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

    private async Task LoadNodeAsync(DiskUsageNode node)
    {
        if (_disposed)
            return;

        _viewCts?.Cancel();
        _viewCts?.Dispose();
        var cts = new CancellationTokenSource();
        _viewCts = cts;
        var version = ++_loadVersion;

        CurrentPath = node.Path;
        CurrentTitle = node.Name;
        CanGoUp = _history.Count > 1;
        SelectedItem = null;

        var items = node.Children
            .Select(child => new DiskUsageItem
            {
                Name = child.Name,
                Path = child.Path,
                Size = child.TotalSize,
                IsDirectory = true,
                Node = child,
            })
            .ToList();

        IReadOnlyList<DiskUsageFile> files;
        try
        {
            files = await Task.Run(() => DiskUsageScanner.GetDirectFiles(
                node.Path, MaxDirectFiles, cts.Token), cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            files = [];
        }

        if (cts.IsCancellationRequested || version != _loadVersion)
            return;

        var topFileSize = files.Sum(file => file.Size);
        items.AddRange(files.Select(file => new DiskUsageItem
        {
            Name = file.Name,
            Path = file.Path,
            Size = file.Size,
            IsDirectory = false,
        }));

        if (node.DirectFileSize > topFileSize)
        {
            items.Add(new DiskUsageItem
            {
                Name = L10n.T("其他文件"),
                Path = node.Path,
                Size = node.DirectFileSize - topFileSize,
                IsDirectory = false,
                IsAggregate = true,
            });
        }

        items.Sort((left, right) =>
        {
            var bySize = right.Size.CompareTo(left.Size);
            return bySize != 0 ? bySize : string.Compare(left.Name, right.Name,
                StringComparison.CurrentCultureIgnoreCase);
        });

        if (items.Count > MaxTiles)
        {
            var overflow = items.Skip(MaxTiles - 1).ToList();
            var overflowSize = overflow.Sum(item => item.Size);
            items = items.Take(MaxTiles - 1).ToList();
            items.Add(new DiskUsageItem
            {
                Name = L10n.T("其他项目"),
                Path = node.Path,
                Size = overflowSize,
                IsDirectory = false,
                IsAggregate = true,
            });
        }

        var total = node.TotalSize;
        for (var index = 0; index < items.Count; index++)
        {
            items[index].Brush = AnalysisPalette.Brush(index);
            items[index].PercentText = total > 0
                ? $"{items[index].Size * 100.0 / total:0.0}%"
                : "0%";
        }

        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        TreemapEntries = items
            .Select(item => new TreemapEntry(item.Id, item.Name, item.Size, item.Path,
                item.IsDirectory, item.IsAggregate))
            .ToList();
        IsEmpty = items.Count == 0;
    }

    private void OnProgress(DiskUsageProgress progress)
    {
        if (_disposed || !IsScanning || _scanCompleted)
            return;
        StatusText = L10n.Format("正在扫描… 已读取 {0} 个文件 · {1}",
            progress.Files, Humanize.Size(progress.Bytes));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        _viewCts?.Dispose();
        _viewCts = null;
    }
}

internal static class AnalysisPalette
{
    private static readonly Color[] Colors =
    [
        Color.Parse("#1C8C78"),
        Color.Parse("#2F6FBF"),
        Color.Parse("#B97822"),
        Color.Parse("#A64F76"),
        Color.Parse("#6A5ACD"),
        Color.Parse("#3F8878"),
        Color.Parse("#4A7FB5"),
        Color.Parse("#A86B3C"),
    ];

    private static readonly IBrush[] Brushes = Colors
        .Select(color => new SolidColorBrush(color))
        .ToArray();

    public static IBrush Brush(int index) => Brushes[Math.Abs(index) % Brushes.Length];

    public static IBrush TextBrush(int index)
    {
        var color = Colors[Math.Abs(index) % Colors.Length];
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luminance > 0.62 ? Brushes2.Black : Brushes2.White;
    }

    private static class Brushes2
    {
        public static readonly IBrush White = new SolidColorBrush(Color.Parse("#FFFFFF"));
        public static readonly IBrush Black = new SolidColorBrush(Color.Parse("#101418"));
    }
}
