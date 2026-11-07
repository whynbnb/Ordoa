using System.Runtime.CompilerServices;
using System.Text;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;

namespace Ordoa.Core.Services;

public sealed record SearchOptions(
    string Query,
    bool IncludeHidden = false,
    int MaxResults = 3000,
    int MaxDepth = 12,
    bool SkipSymlinks = true,
    int MaxContentBytes = 8 * 1024 * 1024,
    Func<FileEntry, bool>? Predicate = null);

public sealed record SearchProgress(int ScannedDirectories, int Results, string CurrentDirectory);

public sealed class SearchService
{
    public async IAsyncEnumerable<FileEntry> SearchAsync(IFileSystem fileSystem, string rootPath, SearchOptions options,
        IProgress<SearchProgress>? progress = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Query))
            yield break;

        var query = SearchQuery.Parse(options.Query);
        if (query.IsEmpty)
            yield break;

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

                if (entry.IsDirectory && query.ShouldPruneDirectory(entry.Name))
                    continue;

                var matches = query.MatchesMetadata(entry);
                if (matches && options.Predicate is not null)
                    matches = options.Predicate(entry);
                if (matches && query.HasContent)
                {
                    if (entry.IsDirectory)
                        matches = false;
                    else
                        matches = await MatchesContentAsync(fileSystem, entry, query.ContentTerms,
                            options.MaxContentBytes, cancellationToken).ConfigureAwait(false);
                }

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

    private static async Task<bool> MatchesContentAsync(IFileSystem fileSystem, FileEntry entry,
        IReadOnlyList<string> terms, int maxBytes, CancellationToken cancellationToken)
    {
        if (entry.Size > maxBytes)
            return false;

        try
        {
            await using var stream = await fileSystem.OpenReadAsync(entry.Path, cancellationToken)
                .ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            var remaining = maxBytes;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    break;
                if (Array.IndexOf(chunk, (byte)0, 0, read) >= 0)
                    return false; // Binary file.
                buffer.Write(chunk, 0, read);
                remaining -= read;
            }

            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                bufferSize: 64 * 1024, leaveOpen: false);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            foreach (var term in terms)
            {
                if (!text.Contains(term, StringComparison.CurrentCultureIgnoreCase))
                    return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
