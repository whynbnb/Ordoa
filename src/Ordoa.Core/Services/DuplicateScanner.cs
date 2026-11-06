using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Ordoa.Core.Localization;

namespace Ordoa.Core.Services;

public enum DuplicateScanStage
{
    Collecting,
    QuickHash,
    FullHash,
    Done,
}

public sealed record DuplicateGroup(long Size, IReadOnlyList<string> Paths)
{
    public int Count => Paths.Count;
    public long WastedBytes => Count <= 1 ? 0 : Size * (Count - 1);
}

public sealed record DuplicateScanProgress(
    DuplicateScanStage Stage,
    int FilesScanned,
    int FilesHashed,
    int GroupsFound,
    long WastedBytes,
    string CurrentPath);

public sealed record DuplicateScanResult(
    IReadOnlyList<DuplicateGroup> Groups,
    int FilesScanned,
    int CandidateFiles,
    long WastedBytes,
    TimeSpan Elapsed);

/// <summary>
/// Finds duplicate files by size first, then a quick fingerprint of the head
/// and tail, and finally a full SHA-256 hash only for surviving candidates.
/// </summary>
public static class DuplicateScanner
{
    private const int BufferSize = 128 * 1024;
    private const int QuickChunkSize = 64 * 1024;

    public static Task<DuplicateScanResult> ScanAsync(string root, long minimumSize = 1,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Task.FromException<DuplicateScanResult>(
                new DirectoryNotFoundException(L10n.T("文件夹不存在")));
        }

        return Task.Run(() => Scan(Path.GetFullPath(root), minimumSize, progress,
            cancellationToken), cancellationToken);
    }

    private static DuplicateScanResult Scan(string root, long minimumSize,
        IProgress<DuplicateScanProgress>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var minimum = Math.Max(1, minimumSize);
        var sizeGroups = new Dictionary<long, List<string>>();
        var filesScanned = 0;
        var filesHashed = 0;
        var lastReport = 0L;
        var reportClock = Stopwatch.StartNew();
        var quickGroups = new Dictionary<(long Size, string Hash), List<string>>();

        void Report(DuplicateScanStage stage, string currentPath, int groupsFound = 0,
            long wastedBytes = 0)
        {
            if (progress is null)
                return;

            var elapsed = reportClock.ElapsedMilliseconds;
            if (elapsed - lastReport < 100)
                return;

            lastReport = elapsed;
            progress.Report(new DuplicateScanProgress(stage, filesScanned, filesHashed,
                groupsFound, wastedBytes, currentPath));
        }

        progress?.Report(new DuplicateScanProgress(DuplicateScanStage.Collecting,
            0, 0, 0, 0, root));

        foreach (var entry in FileScanWalker.EnumerateFiles(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            filesScanned++;
            if (entry.Size < minimum)
            {
                Report(DuplicateScanStage.Collecting, entry.Path);
                continue;
            }

            if (!sizeGroups.TryGetValue(entry.Size, out var paths))
            {
                paths = [];
                sizeGroups[entry.Size] = paths;
            }

            paths.Add(entry.Path);
            Report(DuplicateScanStage.Collecting, entry.Path);
        }

        var candidates = sizeGroups
            .Where(pair => pair.Value.Count > 1)
            .ToList();
        var candidateFiles = candidates.Sum(pair => pair.Value.Count);
        progress?.Report(new DuplicateScanProgress(DuplicateScanStage.QuickHash,
            filesScanned, 0, 0, 0, root));

        foreach (var (size, paths) in candidates)
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = TryComputeHash(path, size, quick: true, cancellationToken);
                if (hash is null)
                    continue;

                filesHashed++;
                var key = (size, hash);
                if (!quickGroups.TryGetValue(key, out var matches))
                {
                    matches = [];
                    quickGroups[key] = matches;
                }

                matches.Add(path);
                Report(DuplicateScanStage.QuickHash, path,
                    quickGroups.Count(pair => pair.Value.Count > 1));
            }
        }

        var possibleGroups = quickGroups
            .Where(pair => pair.Value.Count > 1)
            .ToList();
        var fullGroups = new Dictionary<(long Size, string Hash), List<string>>();
        progress?.Report(new DuplicateScanProgress(DuplicateScanStage.FullHash,
            filesScanned, filesHashed, possibleGroups.Count, 0, root));

        foreach (var (key, paths) in possibleGroups)
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = TryComputeHash(path, key.Size, quick: false, cancellationToken);
                if (hash is null)
                    continue;

                filesHashed++;
                var fullKey = (key.Size, hash);
                if (!fullGroups.TryGetValue(fullKey, out var matches))
                {
                    matches = [];
                    fullGroups[fullKey] = matches;
                }

                matches.Add(path);
                Report(DuplicateScanStage.FullHash, path,
                    fullGroups.Count(pair => pair.Value.Count > 1));
            }
        }

        var groups = new List<DuplicateGroup>();
        foreach (var ((size, _), paths) in fullGroups)
        {
            if (paths.Count < 2)
                continue;

            var ordered = paths
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            if (ordered.Count > 1)
                groups.Add(new DuplicateGroup(size, ordered));
        }

        groups.Sort((left, right) =>
        {
            var byWasted = right.WastedBytes.CompareTo(left.WastedBytes);
            return byWasted != 0 ? byWasted : right.Size.CompareTo(left.Size);
        });

        stopwatch.Stop();
        var wasted = groups.Sum(group => group.WastedBytes);
        progress?.Report(new DuplicateScanProgress(DuplicateScanStage.Done,
            filesScanned, filesHashed, groups.Count, wasted, root));

        return new DuplicateScanResult(groups, filesScanned, candidateFiles, wasted,
            stopwatch.Elapsed);
    }

    private static string? TryComputeHash(string path, long expectedSize, bool quick,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.SequentialScan);
            if (stream.Length != expectedSize)
                return null;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                if (!quick)
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        hash.AppendData(buffer, 0, read);
                    }
                }
                else
                {
                    var firstLength = (int)Math.Min(QuickChunkSize, stream.Length);
                    ReadExactly(stream, buffer, firstLength, cancellationToken);
                    hash.AppendData(buffer, 0, firstLength);

                    if (stream.Length > (long)QuickChunkSize * 2)
                    {
                        stream.Position = stream.Length - QuickChunkSize;
                        ReadExactly(stream, buffer, QuickChunkSize, cancellationToken);
                        hash.AppendData(buffer, 0, QuickChunkSize);
                    }
                    else if (stream.Length > QuickChunkSize)
                    {
                        var remaining = (int)(stream.Length - firstLength);
                        ReadExactly(stream, buffer, remaining, cancellationToken);
                        hash.AppendData(buffer, 0, remaining);
                    }
                }

                return Convert.ToHexString(hash.GetHashAndReset());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, total, count - total);
            if (read <= 0)
                throw new EndOfStreamException();
            total += read;
        }
    }
}
