using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public sealed record ArchiveProgress(int Processed, int Total, long BytesProcessed, long TotalBytes, string CurrentItem);

public static class ArchiveService
{
    public static bool IsSupportedArchive(string fileName)
        => GetArchiveKind(fileName) != ArchiveKind.None;

    private enum ArchiveKind
    {
        None,
        Zip,
        Tar,
        TarGz,
    }

    private static ArchiveKind GetArchiveKind(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (lower.EndsWith(".zip", StringComparison.Ordinal))
            return ArchiveKind.Zip;
        if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
            return ArchiveKind.TarGz;
        if (lower.EndsWith(".tar", StringComparison.Ordinal))
            return ArchiveKind.Tar;
        return ArchiveKind.None;
    }

    public static string GetArchiveStem(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz", StringComparison.Ordinal))
            return fileName[..^7];
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    public static Task CreateAsync(IReadOnlyList<string> paths, string destinationArchive,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var kind = GetArchiveKind(destinationArchive);
            if (kind == ArchiveKind.None)
                throw new NotSupportedException("仅支持 zip、tar 和 tar.gz 格式");

            var items = Scan(paths, destinationArchive, cancellationToken);
            if (items.Count == 0)
                throw new InvalidOperationException("没有可压缩的文件");

            if (kind == ArchiveKind.Zip)
                CreateZip(items, destinationArchive, progress, cancellationToken);
            else
                CreateTar(items, destinationArchive, kind == ArchiveKind.TarGz, progress, cancellationToken);
        }, cancellationToken);
    }

    public static Task<string> ExtractAsync(string archivePath, string destinationParent, bool createSubfolder = true,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var kind = GetArchiveKind(archivePath);
            if (kind == ArchiveKind.None)
                throw new NotSupportedException("仅支持 zip、tar 和 tar.gz 格式");

            var stem = GetArchiveStem(Path.GetFileName(archivePath));
            var destination = createSubfolder
                ? MakeUniqueDirectory(destinationParent, stem)
                : destinationParent;
            Directory.CreateDirectory(destination);

            if (kind == ArchiveKind.Zip)
                ExtractZip(archivePath, destination, progress, cancellationToken);
            else
                ExtractTar(archivePath, destination, kind == ArchiveKind.TarGz, progress, cancellationToken);

            return destination;
        }, cancellationToken);
    }

    private sealed record ArchiveItem(string FullPath, string EntryName, long Size, DateTimeOffset Modified, bool IsDirectory);

    private static List<ArchiveItem> Scan(IReadOnlyList<string> paths, string destinationArchive, CancellationToken cancellationToken)
    {
        var result = new List<ArchiveItem>();
        var destinationFull = Path.GetFullPath(destinationArchive);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                var baseName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                var root = new DirectoryInfo(path);
                foreach (var info in root.EnumerateFileSystemInfos("*", new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                             ReturnSpecialDirectories = false,
                         }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(Path.GetFullPath(info.FullName), destinationFull, StringComparison.Ordinal))
                        continue;
                    var isDirectory = info is DirectoryInfo;
                    var isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                    if (isLink)
                        continue;

                    var relative = Path.GetRelativePath(path, info.FullName).Replace('\\', '/');
                    var entryName = $"{baseName}/{relative}";
                    result.Add(new ArchiveItem(info.FullName, entryName, isDirectory ? 0 : ((FileInfo)info).Length,
                        info.LastWriteTimeUtc, isDirectory));
                }

                result.Add(new ArchiveItem(path, baseName + "/", 0, root.LastWriteTimeUtc, true));
            }
            else if (File.Exists(path))
            {
                if (string.Equals(Path.GetFullPath(path), destinationFull, StringComparison.Ordinal))
                    continue;
                var info = new FileInfo(path);
                result.Add(new ArchiveItem(path, info.Name, info.Length, info.LastWriteTimeUtc, false));
            }
        }

        return result;
    }

    private static void CreateZip(List<ArchiveItem> items, string destination,
        IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            256 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        long doneBytes = 0;
        var totalBytes = items.Sum(i => i.Size);

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            if (item.IsDirectory)
            {
                archive.CreateEntry(item.EntryName.TrimEnd('/') + "/");
                continue;
            }

            var entry = archive.CreateEntry(item.EntryName, CompressionLevel.Optimal);
            entry.LastWriteTime = item.Modified;
            using var source = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                128 * 1024, FileOptions.SequentialScan);
            using var target = entry.Open();
            CopyWithProgress(source, target, chunk =>
            {
                doneBytes += chunk;
                progress?.Report(new ArchiveProgress(index + 1, items.Count, doneBytes, totalBytes, item.EntryName));
            }, cancellationToken);
        }
    }

    private static void CreateTar(List<ArchiveItem> items, string destination, bool gzip,
        IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
    {
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            256 * 1024, FileOptions.Asynchronous);
        Stream sink = gzip ? new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: true) : file;
        try
        {
            using var writer = new TarWriter(sink, TarEntryFormat.Pax, leaveOpen: true);
            long doneBytes = 0;
            var totalBytes = items.Sum(i => i.Size);

            for (var index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = items[index];
                if (item.IsDirectory)
                {
                    var directoryEntry = new PaxTarEntry(TarEntryType.Directory, item.EntryName.TrimEnd('/') + "/")
                    {
                        ModificationTime = item.Modified,
                    };
                    writer.WriteEntry(directoryEntry);
                    continue;
                }

                using var source = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    128 * 1024, FileOptions.SequentialScan);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, item.EntryName)
                {
                    ModificationTime = item.Modified,
                    DataStream = source,
                };
                writer.WriteEntry(entry);
                doneBytes += item.Size;
                progress?.Report(new ArchiveProgress(index + 1, items.Count, doneBytes, totalBytes, item.EntryName));
            }
        }
        finally
        {
            if (gzip)
                sink.Dispose();
        }
    }

    private static void ExtractZip(string archivePath, string destination,
        IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
    {
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var destinationFull = Path.GetFullPath(destination);
        var total = archive.Entries.Count;
        var processed = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            var target = SafeCombine(destinationFull, entry.FullName);
            if (target is null)
            {
                Log.Warn($"跳过不安全的压缩包条目: {entry.FullName}");
                continue;
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            CopyWithProgress(source, output, _ => { }, cancellationToken);
            try { File.SetLastWriteTimeUtc(target, entry.LastWriteTime.UtcDateTime); } catch { /* ignore */ }
            progress?.Report(new ArchiveProgress(processed, total, 0, 0, entry.FullName));
        }
    }

    private static void ExtractTar(string archivePath, string destination, bool gzip,
        IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
    {
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream sourceStream = gzip ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true) : file;
        try
        {
            using var reader = new TarReader(sourceStream, leaveOpen: true);
            var destinationFull = Path.GetFullPath(destination);
            var processed = 0;

            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                var target = SafeCombine(destinationFull, entry.Name);
                if (target is null)
                {
                    Log.Warn($"跳过不安全的压缩包条目: {entry.Name}");
                    continue;
                }

                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    Log.Warn($"跳过压缩包中的链接: {entry.Name}");
                    continue;
                }

                if (entry.DataStream is null)
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                CopyWithProgress(entry.DataStream, output, _ => { }, cancellationToken);
                try { File.SetLastWriteTimeUtc(target, entry.ModificationTime.UtcDateTime); } catch { /* ignore */ }
                progress?.Report(new ArchiveProgress(processed, 0, 0, 0, entry.Name));
            }
        }
        finally
        {
            if (gzip)
                sourceStream.Dispose();
        }
    }

    private static string? SafeCombine(string destinationRoot, string entryName)
    {
        try
        {
            var normalized = entryName.Replace('\\', '/').TrimStart('/');
            var full = Path.GetFullPath(Path.Combine(destinationRoot, normalized));
            var prefix = destinationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.Ordinal) &&
                !string.Equals(full, destinationRoot, StringComparison.Ordinal))
                return null;
            return full;
        }
        catch
        {
            return null;
        }
    }

    private static string MakeUniqueDirectory(string parent, string name)
    {
        Directory.CreateDirectory(parent);
        var candidate = Path.Combine(parent, name);
        if (!Directory.Exists(candidate))
            return candidate;

        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(parent, $"{name} ({i})");
            if (!Directory.Exists(candidate))
                return candidate;
        }
        return Path.Combine(parent, name + " " + Guid.NewGuid().ToString("N")[..6]);
    }

    private static void CopyWithProgress(Stream source, Stream destination, Action<int> onChunk,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            onChunk(read);
        }
    }
}
