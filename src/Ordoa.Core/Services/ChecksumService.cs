using System.Security.Cryptography;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;

namespace Ordoa.Core.Services;

public enum ChecksumAlgorithm
{
    Md5,
    Sha1,
    Sha256,
    Sha512,
}

public static class ChecksumService
{
    public static ChecksumAlgorithm DetectAlgorithm(string? value)
    {
        var length = (value ?? string.Empty).Trim().Length;
        return length switch
        {
            32 => ChecksumAlgorithm.Md5,
            40 => ChecksumAlgorithm.Sha1,
            128 => ChecksumAlgorithm.Sha512,
            _ => ChecksumAlgorithm.Sha256,
        };
    }

    public static string DisplayName(this ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Md5 => "MD5",
        ChecksumAlgorithm.Sha1 => "SHA-1",
        ChecksumAlgorithm.Sha512 => "SHA-512",
        _ => "SHA-256",
    };

    public static Task<string> Sha256Async(IFileSystem fileSystem, string path, long? knownSize = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        => ComputeAsync(fileSystem, path, ChecksumAlgorithm.Sha256, knownSize, progress, cancellationToken);

    public static async Task<string> ComputeAsync(IFileSystem fileSystem, string path,
        ChecksumAlgorithm algorithm, long? knownSize = null, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var algorithmName = algorithm switch
        {
            ChecksumAlgorithm.Md5 => HashAlgorithmName.MD5,
            ChecksumAlgorithm.Sha1 => HashAlgorithmName.SHA1,
            ChecksumAlgorithm.Sha512 => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };

        await using var stream = await fileSystem.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(algorithmName);
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
