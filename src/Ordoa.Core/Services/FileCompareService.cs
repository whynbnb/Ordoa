using System.Security.Cryptography;
using System.Text;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public enum FileCompareKind
{
    Identical,
    Text,
    Binary,
}

public enum CompareLineKind
{
    Same,
    Added,
    Removed,
    Changed,
}

public sealed record CompareLine(int? LeftNumber, int? RightNumber, string LeftText, string RightText,
    CompareLineKind Kind);

public sealed record FileCompareResult(
    FileCompareKind Kind,
    FileEntry Left,
    FileEntry Right,
    bool Identical,
    IReadOnlyList<CompareLine> Lines,
    int Added,
    int Removed,
    long? FirstDifferenceOffset,
    string? LeftHash,
    string? RightHash,
    string Summary);

public static class FileCompareService
{
    private const long MaxTextBytes = 8L * 1024 * 1024;
    private const int MaxDiffCells = 4_000_000;

    public static async Task<FileCompareResult> CompareAsync(IFileSystem fileSystem, FileEntry left, FileEntry right,
        CancellationToken cancellationToken = default)
    {
        if (left.IsDirectory || right.IsDirectory)
            throw new NotSupportedException(L10n.T("只能比较文件"));

        var leftBytes = await ReadAllAsync(fileSystem, left, MaxTextBytes, cancellationToken).ConfigureAwait(false);
        var rightBytes = await ReadAllAsync(fileSystem, right, MaxTextBytes, cancellationToken).ConfigureAwait(false);

        var leftText = TryDecodeText(leftBytes, left);
        var rightText = TryDecodeText(rightBytes, right);
        if (leftText is not null && rightText is not null)
        {
            var lines = DiffLines(leftText, rightText, out var added, out var removed);
            var identical = added == 0 && removed == 0;
            return new FileCompareResult(
                identical ? FileCompareKind.Identical : FileCompareKind.Text,
                left, right, identical, lines, added, removed, null, null, null,
                identical
                    ? L10n.T("两个文本文件内容相同")
                    : L10n.Format("文本差异：+{0} 行，-{1} 行", added, removed));
        }

        return await CompareBinaryAsync(fileSystem, left, right, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FileCompareResult> CompareBinaryAsync(IFileSystem fileSystem, FileEntry left,
        FileEntry right, CancellationToken cancellationToken)
    {
        var hashes = await Task.WhenAll(
            HashAsync(fileSystem, left, cancellationToken),
            HashAsync(fileSystem, right, cancellationToken)).ConfigureAwait(false);
        var identical = string.Equals(hashes[0], hashes[1], StringComparison.OrdinalIgnoreCase) &&
                        left.Size == right.Size;

        long? firstDifference = null;
        if (!identical)
            firstDifference = await FindFirstDifferenceAsync(fileSystem, left, right, cancellationToken)
                .ConfigureAwait(false);

        var summary = identical
            ? L10n.T("两个文件内容相同")
            : firstDifference is { } offset
                ? L10n.Format("二进制文件不同，首个差异位于偏移 {0}", offset)
                : L10n.T("二进制文件不同");

        return new FileCompareResult(identical ? FileCompareKind.Identical : FileCompareKind.Binary,
            left, right, identical, [], 0, 0, firstDifference, hashes[0], hashes[1], summary);
    }

    private static async Task<byte[]> ReadAllAsync(IFileSystem fileSystem, FileEntry entry, long limit,
        CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenReadAsync(entry.Path, cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[128 * 1024];
        while (memory.Length < limit)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - memory.Length)),
                cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static string? TryDecodeText(byte[] bytes, FileEntry entry)
    {
        if (bytes.Length == 0)
            return string.Empty;

        if (Array.IndexOf(bytes, (byte)0) >= 0)
            return null;

        if (!MimeTypes.IsText(entry.Extension) && !LooksLikeText(bytes))
            return null;

        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        var sample = Math.Min(bytes.Length, 4096);
        var control = 0;
        for (var i = 0; i < sample; i++)
        {
            var value = bytes[i];
            if (value == 0)
                return false;
            if (value < 0x20 && value is not (9 or 10 or 13))
                control++;
        }
        return control <= Math.Max(1, sample / 100);
    }

    private static List<CompareLine> DiffLines(string left, string right, out int added, out int removed)
    {
        var leftLines = SplitLines(left);
        var rightLines = SplitLines(right);
        var result = new List<CompareLine>();
        added = 0;
        removed = 0;

        if ((long)leftLines.Length * rightLines.Length > MaxDiffCells)
        {
            // Very large files: compare line by line instead of building a big LCS table.
            var count = Math.Max(leftLines.Length, rightLines.Length);
            for (var i = 0; i < count; i++)
            {
                var hasLeft = i < leftLines.Length;
                var hasRight = i < rightLines.Length;
                if (hasLeft && hasRight && leftLines[i] == rightLines[i])
                {
                    result.Add(new CompareLine(i + 1, i + 1, leftLines[i], rightLines[i], CompareLineKind.Same));
                }
                else if (hasLeft && hasRight)
                {
                    result.Add(new CompareLine(i + 1, i + 1, leftLines[i], rightLines[i],
                        CompareLineKind.Changed));
                    added++;
                    removed++;
                }
                else if (hasLeft)
                {
                    result.Add(new CompareLine(i + 1, null, leftLines[i], string.Empty, CompareLineKind.Removed));
                    removed++;
                }
                else
                {
                    result.Add(new CompareLine(null, i + 1, string.Empty, rightLines[i], CompareLineKind.Added));
                    added++;
                }
            }
            return result;
        }

        var n = leftLines.Length;
        var m = rightLines.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = leftLines[i] == rightLines[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        }

        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (leftLines[x] == rightLines[y])
            {
                result.Add(new CompareLine(x + 1, y + 1, leftLines[x], rightLines[y], CompareLineKind.Same));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new CompareLine(x + 1, null, leftLines[x], string.Empty, CompareLineKind.Removed));
                removed++;
                x++;
            }
            else
            {
                result.Add(new CompareLine(null, y + 1, string.Empty, rightLines[y], CompareLineKind.Added));
                added++;
                y++;
            }
        }

        while (x < n)
        {
            result.Add(new CompareLine(x + 1, null, leftLines[x], string.Empty, CompareLineKind.Removed));
            removed++;
            x++;
        }
        while (y < m)
        {
            result.Add(new CompareLine(null, y + 1, string.Empty, rightLines[y], CompareLineKind.Added));
            added++;
            y++;
        }

        return result;
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static async Task<string> HashAsync(IFileSystem fileSystem, FileEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenReadAsync(entry.Path, cancellationToken).ConfigureAwait(false);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task<long?> FindFirstDifferenceAsync(IFileSystem fileSystem, FileEntry left,
        FileEntry right, CancellationToken cancellationToken)
    {
        await using var leftStream = await fileSystem.OpenReadAsync(left.Path, cancellationToken).ConfigureAwait(false);
        await using var rightStream = await fileSystem.OpenReadAsync(right.Path, cancellationToken).ConfigureAwait(false);
        var leftBuffer = new byte[64 * 1024];
        var rightBuffer = new byte[64 * 1024];
        long offset = 0;
        while (true)
        {
            var leftRead = await leftStream.ReadAsync(leftBuffer, cancellationToken).ConfigureAwait(false);
            var rightRead = await rightStream.ReadAsync(rightBuffer, cancellationToken).ConfigureAwait(false);
            if (leftRead == 0 && rightRead == 0)
                return null;
            var common = Math.Min(leftRead, rightRead);
            for (var i = 0; i < common; i++)
            {
                if (leftBuffer[i] != rightBuffer[i])
                    return offset + i;
            }
            if (leftRead != rightRead)
                return offset + common;
            offset += leftRead;
        }
    }
}
