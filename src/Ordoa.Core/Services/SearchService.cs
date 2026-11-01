using System.Runtime.CompilerServices;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public sealed record SearchOptions(
    string Query,
    bool IncludeHidden = false,
    int MaxResults = 3000,
    int MaxDepth = 12,
    bool SkipSymlinks = true);

public sealed record SearchProgress(int ScannedDirectories, int Results, string CurrentDirectory);

public sealed class SearchService
{
    public async IAsyncEnumerable<FileEntry> SearchAsync(IFileSystem fileSystem, string rootPath, SearchOptions options,
        IProgress<SearchProgress>? progress = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Query))
            yield break;

        var query = options.Query.Trim();
        var isGlob = query.IndexOfAny(['*', '?']) >= 0;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((fileSystem.NormalizePath(rootPath), 0));

        var resultCount = 0;
        var directoryCount = 0;
        var lastReport = DateTime.UtcNow;

        while (queue.Count > 0 && resultCount < options.MaxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();
            directoryCount++;

            IReadOnlyList<FileEntry> entries;
            try
            {
                entries = await fileSystem.ListAsync(directory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!options.IncludeHidden && entry.IsHidden)
                    continue;

                var matches = isGlob
                    ? GlobMatcher.IsMatch(entry.Name, query)
                    : entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);

                if (matches)
                {
                    yield return entry;
                    resultCount++;
                    if (resultCount >= options.MaxResults)
                        yield break;
                }

                if (entry.IsDirectory && !(options.SkipSymlinks && entry.IsSymlink) && depth < options.MaxDepth)
                    queue.Enqueue((entry.Path, depth + 1));
            }

            var now = DateTime.UtcNow;
            if (progress is not null && (now - lastReport).TotalMilliseconds > 150)
            {
                lastReport = now;
                progress.Report(new SearchProgress(directoryCount, resultCount, directory));
            }
        }

        progress?.Report(new SearchProgress(directoryCount, resultCount, rootPath));
    }
}
