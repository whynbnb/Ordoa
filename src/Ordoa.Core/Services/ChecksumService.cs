using System.Security.Cryptography;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;

namespace Ordoa.Core.Services;

public static class ChecksumService
{
    public static async Task<string> Sha256Async(IFileSystem fileSystem, string path, long? knownSize = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var stream = await fileSystem.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
            if (knownSize is > 0)
                progress?.Report(Math.Min(1d, (double)total / knownSize.Value));
        }

        progress?.Report(1);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static async Task<(int Files, int Directories, long Bytes)> MeasureAsync(IFileSystem fileSystem, FileEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (!entry.IsDirectory)
            return (1, 0, Math.Max(0, entry.Size));

        var files = 0;
        var directories = 0;
        long bytes = 0;
        var stack = new Stack<string>();
        stack.Push(entry.Path);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            IReadOnlyList<FileEntry> children;
            try
            {
                children = await fileSystem.ListAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                if (child.IsDirectory)
                {
                    directories++;
                    if (!child.IsSymlink)
                        stack.Push(child.Path);
                }
                else
                {
                    files++;
                    bytes += Math.Max(0, child.Size);
                }
            }
        }

        return (files, directories, bytes);
    }
}
