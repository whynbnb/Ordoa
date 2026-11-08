using System.Text;
using Ordoa.Core.Models;
using Ordoa.Core.Util;
using Ordoa.Core.Localization;

namespace Ordoa.Core.Services;

public enum SearchIndexMode
{
    Whitelist,
    Blacklist,
}

public sealed record SearchIndexConfig(
    SearchIndexMode Mode,
    List<string> Roots,
    List<string> Excludes)
{
    public IReadOnlyList<string> NormalizedRoots =>
        Roots.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<string> NormalizedExcludes =>
        Excludes.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool EquivalentTo(SearchIndexConfig other)
        => Mode == other.Mode &&
           NormalizedRoots.SequenceEqual(other.NormalizedRoots, StringComparer.OrdinalIgnoreCase) &&
           NormalizedExcludes.SequenceEqual(other.NormalizedExcludes, StringComparer.OrdinalIgnoreCase);
}

public sealed class SearchIndexMetadata
{
    public int Version { get; set; } = 1;
    public DateTimeOffset BuiltAt { get; set; }
    public int Count { get; set; }
    public SearchIndexMode Mode { get; set; }
    public List<string> Roots { get; set; } = [];
    public List<string> Excludes { get; set; } = [];
}

public sealed record SearchIndexProgress(int Files, int Directories, string CurrentPath);

/// <summary>
/// A small local search index. Records are split into 64 buckets by the first
/// characters of the lower-cased file name, so a prefix search only reads one
/// bucket instead of the whole index. The index is optional, built on demand,
/// and refreshed through file watchers plus an append-only delta log.
/// </summary>
public sealed class SearchIndexService : IDisposable
{
    private const int BucketCount = 64;
    private const int MaxDeltaEntries = 20000;
    private const int MaxIndexedEntries = 2_000_000;
    private const int BloomWords = 16;
    private const string MetadataFileName = "meta.json";
    private const string DeltaFileName = "delta.log";
    private const string BloomFileName = "bloom.bin";

    private readonly object _sync = new();
    private readonly Dictionary<string, DeltaEntry> _delta = new(StringComparer.Ordinal);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly string _directory;
    private SearchIndexConfig? _config;
    private List<PreparedExclude> _preparedExcludes = [];
    private CancellationTokenSource? _buildCts;
    private StreamWriter? _deltaWriter;
    private bool _disposed;

    public SearchIndexService(string? directory = null)
    {
        _directory = directory ?? Path.Combine(AppPaths.CacheDirectory, "search-index");
    }

    public string DirectoryPath => _directory;
    public SearchIndexConfig? Config => _config;
    public bool IsReady { get; private set; }
    public bool IsBuilding { get; private set; }
    public bool NeedsRebuild { get; private set; }
    public int EntryCount { get; private set; }
    public string StatusText { get; private set; } = L10n.T("索引未启用");
    public event Action? Changed;

    public async Task<bool> EnsureReadyAsync(SearchIndexConfig config,
        IProgress<SearchIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _config = config;
        _preparedExcludes = PrepareExcludes(config);
        StopWatching();
        if (config.Roots.Count == 0)
        {
            IsReady = false;
            StopWatching();
            StatusText = L10n.T("没有配置索引目录");
            Changed?.Invoke();
            return false;
        }

        if (!TryLoadMetadata(config, out var metadata))
        {
            await BuildAsync(config, progress, cancellationToken).ConfigureAwait(false);
            return IsReady;
        }

        EntryCount = metadata.Count;
        LoadDelta();
        IsReady = true;
        StatusText = L10n.Format("索引已就绪：{0} 项", EntryCount);
        StartWatching(config);
        Changed?.Invoke();
        return true;
    }

    public async Task BuildAsync(SearchIndexConfig config,
        IProgress<SearchIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsBuilding)
            return;

        _buildCts?.Dispose();
        _buildCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = _buildCts.Token;
        _config = config;
        _preparedExcludes = PrepareExcludes(config);
        IsBuilding = true;
        IsReady = false;
        StatusText = L10n.T("正在重建索引…");
        Changed?.Invoke();
        StopWatching();

        var temp = _directory + ".build-" + Guid.NewGuid().ToString("N");
        try
        {
            System.IO.Directory.CreateDirectory(temp);
            var writers = new StreamWriter[BucketCount];
            var blooms = new ulong[BucketCount][];
            for (var i = 0; i < BucketCount; i++)
                blooms[i] = new ulong[BloomWords];
            try
            {
                for (var i = 0; i < BucketCount; i++)
                {
                    writers[i] = new StreamWriter(BucketPath(temp, i), append: false,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024);
                }

                var count = 0;
                var files = 0;
                var directories = 0;
                var lastReport = DateTime.UtcNow;

                foreach (var root in config.NormalizedRoots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!System.IO.Directory.Exists(root))
                        continue;

                    var stack = new Stack<string>();
                    stack.Push(root);
                    while (stack.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var current = stack.Pop();
                        IEnumerable<FileSystemInfo> children;
                        try
                        {
                            children = new DirectoryInfo(current).EnumerateFileSystemInfos("*",
                                new EnumerationOptions
                                {
                                    IgnoreInaccessible = true,
                                    RecurseSubdirectories = false,
                                    AttributesToSkip = 0,
                                });
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            continue;
                        }

                        foreach (var child in children)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var name = child.Name;
                            if (name.Length == 0 || ShouldSkip(child.FullName, name))
                                continue;

                            if (child.FullName.StartsWith(_directory, StringComparison.OrdinalIgnoreCase))
                                continue;

                            FileAttributes attributes;
                            bool isDirectory;
                            long size;
                            long modified;
                            try
                            {
                                attributes = child.Attributes;
                                isDirectory = (attributes & FileAttributes.Directory) != 0;
                                size = child is FileInfo file ? file.Length : 0;
                                modified = child.LastWriteTimeUtc.Ticks;
                            }
                            catch
                            {
                                continue;
                            }

                            var isSymlink = (attributes & FileAttributes.ReparsePoint) != 0;
                            var flags = Flags(isDirectory, attributes, name);
                            var fullPath = Path.TrimEndingDirectorySeparator(child.FullName);
                            var bucket = BucketOf(name);
                            WriteRecord(writers[bucket], fullPath, size, modified, flags);
                            BloomAdd(blooms[bucket], name);

                            count++;
                            if (isDirectory)
                                directories++;
                            else
                                files++;

                            if (isDirectory && !isSymlink)
                                stack.Push(fullPath);

                            if (count > MaxIndexedEntries)
                                throw new InvalidOperationException(
                                    L10n.Format("索引项目超过 {0} 个，请缩小索引范围", MaxIndexedEntries));

                            var now = DateTime.UtcNow;
                            if (progress is not null && (now - lastReport).TotalMilliseconds > 200)
                            {
                                lastReport = now;
                                progress.Report(new SearchIndexProgress(files, directories, fullPath));
                            }
                        }
                    }
                }

                for (var i = 0; i < BucketCount; i++)
                {
                    writers[i].Flush();
                    writers[i].Dispose();
                    writers[i] = null!;
                }
                WriteBloom(Path.Combine(temp, BloomFileName), blooms);

                var metadata = new SearchIndexMetadata
                {
                    BuiltAt = DateTimeOffset.Now,
                    Count = count,
                    Mode = config.Mode,
                    Roots = [.. config.NormalizedRoots],
                    Excludes = [.. config.NormalizedExcludes],
                };
                JsonFile.Save(Path.Combine(temp, MetadataFileName), metadata);

                SwapDirectories(temp);
                EntryCount = count;
                lock (_sync)
                {
                    _delta.Clear();
                    CloseDeltaWriter();
                    try { File.Delete(DeltaPath); } catch { /* ignore */ }
                }

                IsReady = true;
                NeedsRebuild = false;
                StatusText = L10n.Format("索引已重建：{0} 项", count);
                StartWatching(config);
            }
            finally
            {
                foreach (var writer in writers)
                    writer?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = L10n.T("索引重建已取消");
            throw;
        }
        catch (Exception ex)
        {
            StatusText = L10n.Format("索引重建失败：{0}", ex.Message);
            Log.Warn("搜索索引重建失败", ex);
        }
        finally
        {
            try
            {
                if (System.IO.Directory.Exists(temp))
                    System.IO.Directory.Delete(temp, recursive: true);
            }
            catch { /* ignore */ }
            IsBuilding = false;
            _buildCts?.Dispose();
            _buildCts = null;
            Changed?.Invoke();
        }
    }

    public async IAsyncEnumerable<FileEntry> SearchAsync(string query, bool includeHidden, int maxResults,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsReady || IsBuilding || string.IsNullOrWhiteSpace(query))
            yield break;

        var parsed = SearchQuery.Parse(query);
        if (parsed.IsEmpty || parsed.HasContent || parsed.Tags.Count > 0 || parsed.Color is not null)
            yield break;

        // Terms are substring matches, not prefixes, so every bucket may hold a
        // match (for example ".hidden" contains "hidden"). Buckets still keep
        // individual files small and let us stream the index with bounded memory.
        var prefix = LiteralPrefix(parsed);
        var blooms = TryLoadBloom();
        var buckets = new List<int>(BucketCount);
        for (var bucket = 0; bucket < BucketCount; bucket++)
        {
            if (blooms is not null && prefix.Length >= 2 && !BloomMayContain(blooms[bucket], prefix))
                continue;
            buckets.Add(bucket);
        }

        var emitted = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bucket in buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = BucketPath(_directory, bucket);
            if (!File.Exists(path))
                continue;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseRecord(line, out var record))
                    continue;
                if (_delta.ContainsKey(record.Path))
                    continue;

                var name = Path.GetFileName(record.Path);
                if (prefix.Length > 0 && !name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var entry = record.ToEntry(name);
                if (!includeHidden && entry.IsHidden)
                    continue;
                if (!parsed.MatchesMetadata(entry))
                    continue;

                seen.Add(entry.Path);
                yield return entry;
                if (++emitted >= maxResults)
                    yield break;
            }
        }

        // Apply the in-memory delta on top of the immutable buckets.
        DeltaEntry[] deltaSnapshot;
        lock (_sync)
            deltaSnapshot = [.. _delta.Values];

        foreach (var delta in deltaSnapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delta.Deleted || seen.Contains(delta.Path))
                continue;

            var name = Path.GetFileName(delta.Path);
            if (prefix.Length > 0 && !name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var entry = delta.ToEntry(name);
            if (!includeHidden && entry.IsHidden)
                continue;
            if (!parsed.MatchesMetadata(entry))
                continue;

            yield return entry;
            if (++emitted >= maxResults)
                yield break;
        }
    }

    public void ApplyChange(string path)
    {
        if (_disposed || !IsReady)
            return;

        try
        {
            var info = TryDescribe(path);
            if (info is null)
            {
                ApplyDelete(path);
                return;
            }

            if (ShouldSkip(info.Value.Path, Path.GetFileName(info.Value.Path)))
                return;

            var delta = new DeltaEntry(false, info.Value.Path, info.Value.Size,
                info.Value.ModifiedUtcTicks, info.Value.Flags);
            AppendDelta(delta);
        }
        catch (Exception ex)
        {
            Log.Warn($"更新搜索索引失败: {path}", ex);
        }
    }

    public void ApplyDelete(string path)
    {
        if (_disposed || !IsReady)
            return;

        AppendDelta(new DeltaEntry(true, path, 0, 0, 0));
    }

    public void Stop()
    {
        try { _buildCts?.Cancel(); } catch { /* ignore */ }
        IsReady = false;
        StopWatching();
        lock (_sync)
            CloseDeltaWriter();
        StatusText = L10n.T("索引未启用");
        Changed?.Invoke();
    }

    public void MarkNeedsRebuild()
    {
        NeedsRebuild = true;
        StatusText = L10n.T("索引可能已过期，建议重建");
        Changed?.Invoke();
    }

    private void AppendDelta(DeltaEntry entry)
    {
        lock (_sync)
        {
            _delta[entry.Path] = entry;
            if (_delta.Count > MaxDeltaEntries)
            {
                NeedsRebuild = true;
                StatusText = L10n.T("索引变更过多，建议重建");
            }

            try
            {
                EnsureDeltaWriter();
                _deltaWriter!.WriteLine(entry.Serialize());
            }
            catch (Exception ex)
            {
                CloseDeltaWriter();
                Log.Warn("写入搜索索引增量失败", ex);
            }
        }

        Changed?.Invoke();
    }

    private void EnsureDeltaWriter()
    {
        if (_deltaWriter is not null)
            return;

        System.IO.Directory.CreateDirectory(_directory);
        var stream = new FileStream(DeltaPath, FileMode.Append, FileAccess.Write, FileShare.Read,
            16 * 1024, FileOptions.None);
        _deltaWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private void CloseDeltaWriter()
    {
        try { _deltaWriter?.Dispose(); } catch { /* ignore */ }
        _deltaWriter = null;
    }

    private void LoadDelta()
    {
        lock (_sync)
        {
            _delta.Clear();
            if (!File.Exists(DeltaPath))
                return;

            try
            {
                foreach (var line in File.ReadLines(DeltaPath))
                {
                    if (DeltaEntry.TryParse(line, out var entry))
                        _delta[entry.Path] = entry;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取搜索索引增量失败", ex);
            }

            if (_delta.Count > MaxDeltaEntries)
            {
                NeedsRebuild = true;
                StatusText = L10n.T("索引变更过多，建议重建");
            }
        }
    }

    private bool TryLoadMetadata(SearchIndexConfig config, out SearchIndexMetadata metadata)
    {
        metadata = new SearchIndexMetadata();
        try
        {
            var path = Path.Combine(_directory, MetadataFileName);
            if (!File.Exists(path))
                return false;
            metadata = JsonFile.Load<SearchIndexMetadata>(path) ?? new SearchIndexMetadata();
            if (metadata.Version != 1 || metadata.Count <= 0)
                return false;
            if (!config.EquivalentTo(new SearchIndexConfig(metadata.Mode, metadata.Roots, metadata.Excludes)))
                return false;

            // Watchers only cover changes made while Ordoa is running. Rebuild
            // periodically so edits made while the app was closed are picked up.
            if (metadata.BuiltAt != default &&
                DateTimeOffset.Now - metadata.BuiltAt > TimeSpan.FromDays(1))
            {
                NeedsRebuild = true;
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void StartWatching(SearchIndexConfig config)
    {
        StopWatching();
        if (_disposed)
            return;

        foreach (var root in config.NormalizedRoots)
        {
            if (!System.IO.Directory.Exists(root))
                continue;

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => ApplyChange(e.FullPath);
                watcher.Changed += (_, e) => ApplyChange(e.FullPath);
                watcher.Deleted += (_, e) => ApplyDelete(e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    ApplyDelete(e.OldFullPath);
                    ApplyChange(e.FullPath);
                };
                watcher.Error += (_, _) => MarkNeedsRebuild();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Log.Warn($"无法监听索引目录: {root}", ex);
            }
        }
    }

    private void StopWatching()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch { /* ignore */ }
        }
        _watchers.Clear();
    }

    private static string LiteralPrefix(SearchQuery query)
    {
        // The longest literal term gives the bloom filter the most selective
        // bigrams, reducing the number of buckets read for a query.
        var best = string.Empty;
        foreach (var term in query.Terms)
        {
            if (term.Length < 2 || term.IndexOfAny(['*', '?']) >= 0)
                continue;
            if (term.Length > best.Length)
                best = term;
        }

        return best;
    }

    private static bool BloomMayContain(ulong[] bloom, string term)
    {
        var lower = term.ToLowerInvariant();
        if (lower.Length < 2)
            return true;

        for (var i = 0; i + 1 < lower.Length; i++)
        {
            HashBigram(lower[i], lower[i + 1], out var first, out var second);
            if ((bloom[first >> 6] & (1UL << (first & 63))) == 0 ||
                (bloom[second >> 6] & (1UL << (second & 63))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void BloomAdd(ulong[] bloom, string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Length < 2)
            return;

        for (var i = 0; i + 1 < lower.Length; i++)
        {
            HashBigram(lower[i], lower[i + 1], out var first, out var second);
            bloom[first >> 6] |= 1UL << (first & 63);
            bloom[second >> 6] |= 1UL << (second & 63);
        }
    }

    private static void HashBigram(char first, char second, out int a, out int b)
    {
        var value = ((uint)first << 16) | second;
        var h1 = value * 2654435761u;
        var h2 = (value ^ (value >> 15)) * 2246822519u;
        a = (int)(h1 % 1024);
        b = (int)(h2 % 1024);
    }

    private static void WriteBloom(string path, ulong[][] blooms)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        foreach (var bloom in blooms)
        {
            foreach (var word in bloom)
                writer.Write(word);
        }
    }

    private ulong[][]? TryLoadBloom()
    {
        try
        {
            var path = Path.Combine(_directory, BloomFileName);
            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var blooms = new ulong[BucketCount][];
            for (var bucket = 0; bucket < BucketCount; bucket++)
            {
                blooms[bucket] = new ulong[BloomWords];
                for (var word = 0; word < BloomWords; word++)
                    blooms[bucket][word] = reader.ReadUInt64();
            }
            return blooms;
        }
        catch
        {
            return null;
        }
    }

    private static int BucketOf(string name)
    {
        var first = name.Length > 0 ? char.ToLowerInvariant(name[0]) : '\0';
        var second = name.Length > 1 ? char.ToLowerInvariant(name[1]) : '\0';
        return (first * 31 + second) & (BucketCount - 1);
    }

    private static string BucketPath(string directory, int bucket)
        => Path.Combine(directory, $"bucket-{bucket:00}.idx");

    private string DeltaPath => Path.Combine(_directory, DeltaFileName);

    private static List<PreparedExclude> PrepareExcludes(SearchIndexConfig config)
    {
        // Whitelist mode means exactly the roots listed; exclusion rules are
        // the blacklist-mode extra that removes sub-paths from those roots.
        if (config.Mode != SearchIndexMode.Blacklist)
            return [];

        var result = new List<PreparedExclude>(config.NormalizedExcludes.Count);
        foreach (var raw in config.NormalizedExcludes)
        {
            var isGlob = raw.IndexOfAny(['*', '?']) >= 0;
            string? fullPath = null;
            if (!isGlob)
            {
                try { fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw)); }
                catch { /* keep the name-only match */ }
            }
            result.Add(new PreparedExclude(raw, fullPath, isGlob));
        }
        return result;
    }

    private bool ShouldSkip(string path, string name)
    {
        if (_preparedExcludes.Count == 0)
            return false;

        var normalized = Path.TrimEndingDirectorySeparator(path);
        foreach (var exclude in _preparedExcludes)
        {
            if (exclude.IsGlob)
            {
                if (GlobMatcher.IsMatch(name, exclude.Raw) || GlobMatcher.IsMatch(normalized, exclude.Raw))
                    return true;
                continue;
            }

            if (name.Equals(exclude.Raw, StringComparison.OrdinalIgnoreCase))
                return true;
            if (exclude.FullPath is { } fullPath &&
                (normalized.Equals(fullPath, StringComparison.OrdinalIgnoreCase) ||
                 normalized.StartsWith(fullPath + Path.DirectorySeparatorChar,
                     StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteRecord(StreamWriter writer, string path, long size, long modifiedTicks, int flags)
    {
        writer.Write(Escape(path));
        writer.Write('\t');
        writer.Write(size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.Write('\t');
        writer.Write(modifiedTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.Write('\t');
        writer.Write(flags.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.Write('\n');
    }

    private static bool TryParseRecord(string line, out IndexRecord record)
    {
        record = default;
        var parts = line.Split('\t');
        if (parts.Length != 4)
            return false;
        if (!long.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var size) ||
            !long.TryParse(parts[2], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var modified) ||
            !int.TryParse(parts[3], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var flags))
        {
            return false;
        }

        record = new IndexRecord(Unescape(parts[0]), size, modified, flags);
        return record.Path.Length > 0;
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                builder.Append(value[i] switch
                {
                    't' => '\t',
                    'n' => '\n',
                    'r' => '\r',
                    _ => value[i],
                });
            }
            else
            {
                builder.Append(value[i]);
            }
        }
        return builder.ToString();
    }

    private static IndexPathInfo? TryDescribe(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isSymlink = (attributes & FileAttributes.ReparsePoint) != 0;
            if (isDirectory)
            {
                var info = new DirectoryInfo(path);
                return new IndexPathInfo(Path.TrimEndingDirectorySeparator(info.FullName), 0,
                    info.LastWriteTimeUtc.Ticks, Flags(isDirectory: true, attributes, info.Name), true, isSymlink);
            }

            var file = new FileInfo(path);
            return new IndexPathInfo(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks,
                Flags(isDirectory: false, attributes, file.Name), false, false);
        }
        catch
        {
            return null;
        }
    }

    private static int Flags(bool isDirectory, FileAttributes attributes, string name)
    {
        var flags = 0;
        if (isDirectory)
            flags |= 1;
        if (name.StartsWith('.') || (attributes & FileAttributes.Hidden) != 0)
            flags |= 2;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            flags |= 4;
        return flags;
    }

    private void SwapDirectories(string temp)
    {
        var old = _directory + ".old";
        try
        {
            if (System.IO.Directory.Exists(old))
                System.IO.Directory.Delete(old, recursive: true);
            if (System.IO.Directory.Exists(_directory))
                System.IO.Directory.Move(_directory, old);
            System.IO.Directory.Move(temp, _directory);
            if (System.IO.Directory.Exists(old))
                System.IO.Directory.Delete(old, recursive: true);
        }
        catch
        {
            if (!System.IO.Directory.Exists(_directory) && System.IO.Directory.Exists(old))
                System.IO.Directory.Move(old, _directory);
            throw;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        StopWatching();
        lock (_sync)
            CloseDeltaWriter();
        try { _buildCts?.Cancel(); } catch { /* ignore */ }
        try { _buildCts?.Dispose(); } catch { /* ignore */ }
    }

    private readonly record struct PreparedExclude(string Raw, string? FullPath, bool IsGlob);

    private readonly record struct IndexPathInfo(string Path, long Size, long ModifiedUtcTicks, int Flags,
        bool IsDirectory, bool IsSymlink);

    private readonly record struct IndexRecord(string Path, long Size, long ModifiedUtcTicks, int Flags)
    {
        public FileEntry ToEntry(string name) => new()
        {
            Name = name,
            Path = Path,
            Kind = (Flags & 1) != 0 ? EntryKind.Directory : EntryKind.File,
            Size = Size,
            Modified = new DateTimeOffset(ModifiedUtcTicks, TimeSpan.Zero),
            IsHidden = (Flags & 2) != 0,
            IsSymlink = (Flags & 4) != 0,
        };
    }

    private readonly record struct DeltaEntry(bool Deleted, string Path, long Size, long ModifiedUtcTicks, int Flags)
    {
        public FileEntry ToEntry(string name) => new()
        {
            Name = name,
            Path = Path,
            Kind = (Flags & 1) != 0 ? EntryKind.Directory : EntryKind.File,
            Size = Size,
            Modified = new DateTimeOffset(ModifiedUtcTicks, TimeSpan.Zero),
            IsHidden = (Flags & 2) != 0,
            IsSymlink = (Flags & 4) != 0,
        };

        public string Serialize()
            => (Deleted ? "D\t" : "U\t") + Escape(Path) + "\t" +
               Size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" +
               ModifiedUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" +
               Flags.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static bool TryParse(string line, out DeltaEntry entry)
        {
            entry = default;
            var parts = line.Split('\t');
            if (parts.Length < 2)
                return false;

            var deleted = parts[0] == "D";
            var path = Unescape(parts[1]);
            if (path.Length == 0)
                return false;

            long size = 0;
            long modified = 0;
            var flags = 0;
            if (!deleted)
            {
                if (parts.Length != 5 ||
                    !long.TryParse(parts[2], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out size) ||
                    !long.TryParse(parts[3], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out modified) ||
                    !int.TryParse(parts[4], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out flags))
                {
                    return false;
                }
            }

            entry = new DeltaEntry(deleted, path, size, modified, flags);
            return true;
        }
    }
}
