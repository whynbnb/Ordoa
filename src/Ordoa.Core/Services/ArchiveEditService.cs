using System.IO.Compression;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Localization;
using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

/// <summary>Modifies zip, tar and 7z archives in place using 7-Zip when available.</summary>
public static class ArchiveEditService
{
    public static bool CanWrite(ArchiveFormat format)
        => format is ArchiveFormat.Zip or ArchiveFormat.Tar or ArchiveFormat.TarGz or ArchiveFormat.SevenZip;

    public static Task DeleteAsync(string archivePath, ArchiveFormat format, IReadOnlyList<string> entryPaths,
        string? password = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            EnsureWritable(format);
            if (SevenZipTool.IsAvailable)
            {
                SevenZipTool.Delete(archivePath, entryPaths, cancellationToken, password);
                return;
            }
            if (format == ArchiveFormat.Zip)
            {
                DeleteZipEntries(archivePath, entryPaths);
                return;
            }
            throw NoTool();
        }, cancellationToken);
    }

    public static Task RenameAsync(string archivePath, ArchiveFormat format, string oldPath, string newPath,
        string? password = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            EnsureWritable(format);
            if (SevenZipTool.IsAvailable)
            {
                SevenZipTool.Rename(archivePath, oldPath, newPath, cancellationToken, password);
                return;
            }
            if (format == ArchiveFormat.Zip)
            {
                RenameZipEntry(archivePath, oldPath, newPath);
                return;
            }
            throw NoTool();
        }, cancellationToken);
    }

    public static async Task AddAsync(string archivePath, ArchiveFormat format, string archiveDirectory,
        IReadOnlyList<(string LocalPath, string Name)> files, string? password = null,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable(format);
        if (files.Count == 0)
            return;

        if (!SevenZipTool.IsAvailable)
        {
            if (format != ArchiveFormat.Zip)
                throw NoTool();
            AddZipEntries(archivePath, archiveDirectory, files);
            return;
        }

        var staging = Path.Combine(AppPaths.CacheDirectory, "archive-add", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var relative = new List<string>();
            foreach (var (localPath, name) in files)
            {
                var archiveName = CombineArchivePath(archiveDirectory, name);
                var target = Path.Combine(staging, archiveName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    LinkService.CreateHardLink(localPath, target);
                }
                catch
                {
                    File.Copy(localPath, target, true);
                }
                relative.Add(archiveName);
            }

            await Task.Run(() => SevenZipTool.Add(archivePath, staging, relative, cancellationToken, password),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { /* best effort */ }
        }
    }

    public static string CombineArchivePath(string directory, string name)
    {
        var normalized = (directory ?? "/").Replace('\\', '/').Trim('/');
        var cleanName = name.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? cleanName : normalized + "/" + cleanName;
    }

    private static void DeleteZipEntries(string archivePath, IReadOnlyList<string> entryPaths)
    {
        using var archive = ZipEncodingHelper.OpenUpdate(archivePath);
        foreach (var entry in archive.Entries.ToList())
        {
            if (!MatchesSelection(entry.FullName, entryPaths))
                continue;
            entry.Delete();
        }
    }

    private static void RenameZipEntry(string archivePath, string oldPath, string newPath)
    {
        using var archive = ZipEncodingHelper.OpenUpdate(archivePath);
        var oldName = oldPath.Replace('\\', '/').Trim('/');
        var newName = newPath.Replace('\\', '/').Trim('/');
        var matches = archive.Entries
            .Where(entry => entry.FullName.Replace('\\', '/').Trim('/') == oldName ||
                            entry.FullName.Replace('\\', '/').Trim('/').StartsWith(oldName + "/",
                                StringComparison.Ordinal))
            .ToList();
        foreach (var entry in matches)
        {
            var suffix = entry.FullName.Replace('\\', '/').Trim('/')[oldName.Length..];
            var targetName = newName + suffix;
            if (string.IsNullOrEmpty(entry.Name))
            {
                entry.Delete();
                continue;
            }

            var source = entry.Open();
            var replacement = archive.CreateEntry(targetName);
            replacement.LastWriteTime = entry.LastWriteTime;
            using (var output = replacement.Open())
                source.CopyTo(output);
            source.Dispose();
            entry.Delete();
        }
    }

    private static void AddZipEntries(string archivePath, string archiveDirectory,
        IReadOnlyList<(string LocalPath, string Name)> files)
    {
        using var archive = ZipEncodingHelper.OpenUpdate(archivePath);
        foreach (var (localPath, name) in files)
        {
            var entryName = CombineArchivePath(archiveDirectory, name);
            var existing = archive.GetEntry(entryName);
            existing?.Delete();
            var entry = archive.CreateEntry(entryName);
            entry.LastWriteTime = File.GetLastWriteTime(localPath);
            using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = entry.Open();
            source.CopyTo(output);
        }
    }

    private static bool MatchesSelection(string entryName, IReadOnlyList<string> entryPaths)
    {
        var name = entryName.Replace('\\', '/').Trim('/');
        foreach (var selected in entryPaths)
        {
            var trimmed = selected.Replace('\\', '/').Trim('/');
            var directory = trimmed.EndsWith("/*", StringComparison.Ordinal) ? trimmed[..^2] : trimmed;
            if (string.Equals(name, trimmed, StringComparison.Ordinal))
                return true;
            if (string.Equals(name, directory, StringComparison.Ordinal))
                return true;
            if (name.StartsWith(directory + "/", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void EnsureWritable(ArchiveFormat format)
    {
        if (!CanWrite(format))
            throw new NotSupportedException(L10n.T("此压缩包格式不支持写入"));
    }

    private static NotSupportedException NoTool()
        => new(L10n.T("需要安装 7z 命令行工具才能修改此压缩包"));
}
