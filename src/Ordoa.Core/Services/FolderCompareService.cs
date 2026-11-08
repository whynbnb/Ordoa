using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Localization;

namespace Ordoa.Core.Services;

public enum FolderCompareState
{
    Same,
    OnlyLeft,
    OnlyRight,
    Different,
    LeftNewer,
    RightNewer,
    TypeMismatch,
}

public sealed record FolderCompareOptions(
    bool IncludeHidden = false,
    bool CompareContents = false,
    int MaxDepth = 32,
    int MaxEntries = 20000);

public sealed record FolderCompareProgress(
    int Scanned,
    int Differences,
    string CurrentPath);

public sealed record FolderCompareItem(
    string RelativePath,
    FileEntry? LeftEntry,
    FileEntry? RightEntry,
    FolderCompareState State)
{
    public bool LeftIsDirectory => LeftEntry?.IsDirectory == true;
    public bool RightIsDirectory => RightEntry?.IsDirectory == true;
    public bool CanCopyRight => State is FolderCompareState.OnlyLeft or FolderCompareState.Different
        or FolderCompareState.LeftNewer && LeftEntry is { IsDirectory: false };
    public bool CanCopyLeft => State is FolderCompareState.OnlyRight or FolderCompareState.Different
        or FolderCompareState.RightNewer && RightEntry is { IsDirectory: false };
}

/// <summary>Recursively compares two folders and reports relative-path differences.</summary>
public sealed class FolderCompareService
{
    public async Task<IReadOnlyList<FolderCompareItem>> CompareAsync(
        IFileSystem leftFileSystem, string leftRoot,
        IFileSystem rightFileSystem, string rightRoot,
        FolderCompareOptions options,
        IProgress<FolderCompareProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        leftRoot = leftFileSystem.NormalizePath(leftRoot);
        rightRoot = rightFileSystem.NormalizePath(rightRoot);

        var left = await ScanAsync(leftFileSystem, leftRoot, options, cancellationToken).ConfigureAwait(false);
        var right = await ScanAsync(rightFileSystem, rightRoot, options, cancellationToken).ConfigureAwait(false);

        var keys = left.Keys.Concat(right.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        var result = new List<FolderCompareItem>(keys.Count);
        var differences = 0;
        var lastReport = DateTime.UtcNow;
        for (var i = 0; i < keys.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = keys[i];
            left.TryGetValue(key, out var leftEntry);
            right.TryGetValue(key, out var rightEntry);

            FolderCompareState state;
            if (leftEntry is null)
                state = FolderCompareState.OnlyRight;
            else if (rightEntry is null)
                state = FolderCompareState.OnlyLeft;
            else
                state = await CompareEntriesAsync(leftFileSystem, rightFileSystem, leftEntry!, rightEntry!,
                    options, cancellationToken).ConfigureAwait(false);

            if (state != FolderCompareState.Same)
                differences++;
            result.Add(new FolderCompareItem(key, leftEntry, rightEntry, state));

            var now = DateTime.UtcNow;
            if (progress is not null && (now - lastReport).TotalMilliseconds > 150)
            {
                lastReport = now;
                progress.Report(new FolderCompareProgress(i + 1, differences, key));
            }
        }

        progress?.Report(new FolderCompareProgress(keys.Count, differences, string.Empty));
        return result;
    }

    private static async Task<Dictionary<string, FileEntry>> ScanAsync(
        IFileSystem fileSystem, string root,
        FolderCompareOptions options, CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        var stack = new Stack<(string Path, string Prefix, int Depth)>();
        stack.Push((root, string.Empty, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, prefix, depth) = stack.Pop();

            IReadOnlyList<FileEntry> children;
            try
            {
                children = await fileSystem.ListAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!options.IncludeHidden && child.IsHidden)
                    continue;

                var relative = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
                if (!entries.TryAdd(relative, child))
                    continue;
                if (entries.Count > options.MaxEntries)
                    throw new InvalidOperationException(
                        L10n.Format("比较项目超过 {0} 个，请缩小范围后再试", options.MaxEntries));

                if (child.IsDirectory && !child.IsSymlink && depth < options.MaxDepth)
                    stack.Push((child.Path, relative, depth + 1));
            }
        }

        return entries;
    }

    private static async Task<FolderCompareState> CompareEntriesAsync(
        IFileSystem leftFileSystem, IFileSystem rightFileSystem,
        FileEntry left, FileEntry right,
        FolderCompareOptions options, CancellationToken cancellationToken)
    {
        if (left.IsDirectory != right.IsDirectory)
            return FolderCompareState.TypeMismatch;
        if (left.IsDirectory)
            return FolderCompareState.Same;

        if (left.Size != right.Size)
            return FolderCompareState.Different;

        if (options.CompareContents && left.Size > 0)
        {
            try
            {
                var leftHash = await ChecksumService.ComputeAsync(leftFileSystem, left.Path,
                    ChecksumAlgorithm.Sha256, left.Size, null, cancellationToken).ConfigureAwait(false);
                var rightHash = await ChecksumService.ComputeAsync(rightFileSystem, right.Path,
                    ChecksumAlgorithm.Sha256, right.Size, null, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(leftHash, rightHash, StringComparison.Ordinal))
                    return FolderCompareState.Different;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return FolderCompareState.Different;
            }
        }

        if (left.Modified is not { } leftModified || right.Modified is not { } rightModified)
            return FolderCompareState.Same;

        var delta = leftModified - rightModified;
        if (delta > TimeSpan.FromSeconds(2))
            return FolderCompareState.LeftNewer;
        if (delta < TimeSpan.FromSeconds(-2))
            return FolderCompareState.RightNewer;
        return FolderCompareState.Same;
    }
}
