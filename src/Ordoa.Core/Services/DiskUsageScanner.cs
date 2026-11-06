using System.Diagnostics;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public sealed class DiskUsageNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public long DirectFileSize { get; set; }
    public long TotalSize { get; set; }
    public int FileCount { get; set; }
    public List<DiskUsageNode> Children { get; } = [];
}

public sealed record DiskUsageFile(string Path, string Name, long Size);

public sealed record DiskUsageProgress(int Directories, int Files, long Bytes, string CurrentPath);

public sealed record DiskUsageResult(
    DiskUsageNode Root,
    IReadOnlyList<DiskUsageFile> LargestFiles,
    int DirectoryCount,
    int FileCount,
    long TotalBytes,
    TimeSpan Elapsed);

/// <summary>
/// Computes recursive directory sizes in one pass. Only directories are kept
/// in the tree; direct files are loaded on demand for the folder being viewed.
/// </summary>
public static class DiskUsageScanner
{
    private const int MaxLargestFiles = 200;
    private const int MaxDepth = 1024;

    public static Task<DiskUsageResult> ScanAsync(string root,
        IProgress<DiskUsageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Task.FromException<DiskUsageResult>(
                new DirectoryNotFoundException(L10n.T("文件夹不存在")));
        }

        return Task.Run(() => Scan(root, progress, cancellationToken), cancellationToken);
    }

    public static IReadOnlyList<DiskUsageFile> GetDirectFiles(string directory, int max,
        CancellationToken cancellationToken = default)
    {
        var files = new List<DiskUsageFile>();
        foreach (var path in FileScanWalker.GetFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    continue;
                files.Add(new DiskUsageFile(path, info.Name, info.Length));
            }
            catch
            {
                // Skip files that disappear or cannot be read.
            }
        }

        return files
            .OrderByDescending(file => file.Size)
            .ThenBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(1, max))
            .ToList();
    }

    private static DiskUsageResult Scan(string root, IProgress<DiskUsageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var state = new ScanState(progress, cancellationToken);
        var rootNode = ScanDirectory(Path.GetFullPath(root), DirectoryName(root), 0, state);
        stopwatch.Stop();

        var largest = state.LargestFiles.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(file => file.Size)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

        progress?.Report(new DiskUsageProgress(state.DirectoryCount, state.FileCount,
            state.Bytes, rootNode.Path));

        return new DiskUsageResult(rootNode, largest, state.DirectoryCount, state.FileCount,
            rootNode.TotalSize, stopwatch.Elapsed);
    }

    private static DiskUsageNode ScanDirectory(string path, string name, int depth,
        ScanState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        var node = new DiskUsageNode { Name = name, Path = path };
        state.DirectoryCount++;

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(path).GetFileSystemInfos("*", FileScanWalker.Options);
        }
        catch
        {
            entries = [];
        }

        var directories = new List<DirectoryInfo>();
        foreach (var entry in entries)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            if (entry is DirectoryInfo directory)
            {
                directories.Add(directory);
                continue;
            }

            if (entry is not FileInfo file)
                continue;

            long size;
            try
            {
                size = file.Length;
            }
            catch
            {
                continue;
            }

            node.DirectFileSize += size;
            node.FileCount++;
            state.FileCount++;
            state.Bytes += size;
            AddLargestFile(state, new DiskUsageFile(file.FullName, file.Name, size));
        }

        if (depth < MaxDepth)
        {
            foreach (var directory in directories)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var child = ScanDirectory(directory.FullName, directory.Name, depth + 1, state);
                node.Children.Add(child);
                node.TotalSize += child.TotalSize;
                node.FileCount += child.FileCount;
            }
        }

        node.TotalSize += node.DirectFileSize;
        state.MaybeReport(path, node.Path);
        return node;
    }

    private static void AddLargestFile(ScanState state, DiskUsageFile file)
    {
        if (file.Size <= 0)
            return;

        if (state.LargestFiles.Count < MaxLargestFiles)
        {
            state.LargestFiles.Enqueue(file, file.Size);
            return;
        }

        if (state.LargestFiles.TryPeek(out _, out var smallest) && file.Size > smallest)
        {
            state.LargestFiles.Dequeue();
            state.LargestFiles.Enqueue(file, file.Size);
        }
    }

    private static string DirectoryName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private sealed class ScanState(IProgress<DiskUsageProgress>? progress,
        CancellationToken cancellationToken)
    {
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();
        private long _lastReport;

        public CancellationToken CancellationToken { get; } = cancellationToken;
        public IProgress<DiskUsageProgress>? Progress { get; } = progress;
        public int DirectoryCount { get; set; }
        public int FileCount { get; set; }
        public long Bytes { get; set; }
        public PriorityQueue<DiskUsageFile, long> LargestFiles { get; } = new();

        public void MaybeReport(string currentPath, string displayPath)
        {
            var elapsed = _sinceReport.ElapsedMilliseconds;
            if (elapsed - _lastReport < 100)
                return;

            _lastReport = elapsed;
            Progress?.Report(new DiskUsageProgress(DirectoryCount, FileCount, Bytes,
                string.IsNullOrEmpty(displayPath) ? currentPath : displayPath));
        }
    }
}
