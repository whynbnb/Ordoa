using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Services;
using Ordoa.Core.Util;
using Sdcb.SimdCodecs.Abstractions;
using SkiaSharp;
using SimdCodecsFacade = Sdcb.SimdCodecs.SimdCodecs;

namespace Ordoa.App.Services;

public enum PreviewKind
{
    Loading,
    Image,
    Text,
    Video,
    Audio,
    Archive,
    Folder,
    Document,
    Info,
}

public sealed record PreviewDetail(string Label, string Value);

public sealed record PreviewRow(
    string Name,
    string Detail,
    bool IsDirectory,
    Geometry? Icon,
    IBrush? IconBrush);

public sealed record PreviewContent(
    PreviewKind Kind,
    Bitmap? Image = null,
    string? Text = null,
    string? Notice = null,
    IReadOnlyList<PreviewDetail>? Details = null,
    IReadOnlyList<PreviewRow>? Rows = null);

/// <summary>
/// Lightweight Quick Look style file inspection. Content is decoded only up to a
/// bounded size; ffmpeg is optional and never bundled.
/// </summary>
public sealed class PreviewService
{
    private const int MaxImageDimension = 2048;
    private const long MaxImageBytes = 128L * 1024 * 1024;
    private const int MaxTextBytes = 512 * 1024;
    private const int MaxRows = 200;
    private const int MaxHexBytes = 512;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<PreviewContent> LoadAsync(IFileSystem fileSystem, FileEntry entry,
        string? ffmpegPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var details = BuildBaseDetails(fileSystem, entry);

        if (entry.IsDirectory)
            return await PreviewFolderAsync(fileSystem, entry, details, cancellationToken).ConfigureAwait(true);

        if (entry.Size == 0)
        {
            return new PreviewContent(PreviewKind.Info, Notice: L10n.T("这是一个空文件。"), Details: details);
        }

        var extension = entry.Extension;
        var localPath = LocalPathOf(entry);

        if (extension == "svg")
            return await PreviewTextAsync(fileSystem, entry, details, forceText: true, cancellationToken)
                .ConfigureAwait(true);

        if (IsImageExtension(extension))
            return await PreviewImageAsync(fileSystem, entry, details, localPath, ffmpegPath, cancellationToken)
                .ConfigureAwait(true);

        if (IsVideoExtension(extension) || entry.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return await PreviewMediaAsync(entry, details, localPath, ffmpegPath, video: true, cancellationToken)
                .ConfigureAwait(true);
        }

        if (IsAudioExtension(extension) || entry.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return await PreviewMediaAsync(entry, details, localPath, ffmpegPath, video: false, cancellationToken)
                .ConfigureAwait(true);
        }

        if (MimeTypes.IsArchive(extension))
        {
            var archive = await TryArchiveAsync(entry, localPath, details, cancellationToken).ConfigureAwait(true);
            if (archive is not null)
                return archive;
        }

        if (extension == "pdf")
            return await PreviewPdfAsync(entry, details, localPath, cancellationToken).ConfigureAwait(true);

        if (IsOfficeExtension(extension))
            return await PreviewDocumentAsync(entry, details, localPath, cancellationToken).ConfigureAwait(true);

        if (MimeTypes.IsText(extension))
            return await PreviewTextAsync(fileSystem, entry, details, forceText: true, cancellationToken)
                .ConfigureAwait(true);

        // Unknown extension: sniff content instead of trusting the name.
        var prefix = await ReadPrefixAsync(fileSystem, entry, 32, cancellationToken).ConfigureAwait(true);
        if (IsImageHeader(prefix))
            return await PreviewImageAsync(fileSystem, entry, details, localPath, ffmpegPath, cancellationToken)
                .ConfigureAwait(true);
        if (IsPdfHeader(prefix))
            return await PreviewPdfAsync(entry, details, localPath, cancellationToken).ConfigureAwait(true);
        if (IsZipHeader(prefix))
        {
            var archive = await TryArchiveAsync(entry, localPath, details, cancellationToken, zipOnly: true)
                .ConfigureAwait(true);
            if (archive is not null)
                return archive;
        }
        if (IsTextHeader(prefix))
            return await PreviewTextAsync(fileSystem, entry, details, forceText: false, cancellationToken)
                .ConfigureAwait(true);

        var bytes = await ReadLimitedAsync(fileSystem, entry, MaxHexBytes, cancellationToken).ConfigureAwait(true);
        details.Add(new PreviewDetail(L10n.T("文件头"), $"{bytes.Length} " + L10n.T("字节")));
        return new PreviewContent(
            PreviewKind.Info,
            Text: BuildHexDump(bytes),
            Notice: L10n.T("此格式没有内置预览，以下显示文件头。"),
            Details: details);
    }

    private async Task<PreviewContent> PreviewImageAsync(IFileSystem fileSystem, FileEntry entry,
        List<PreviewDetail> details, string? localPath, string? ffmpegPath, CancellationToken cancellationToken)
    {
        if (entry.Size > MaxImageBytes)
        {
            return new PreviewContent(PreviewKind.Image,
                Notice: L10n.T("图片过大，未加载预览。"), Details: details);
        }

        Bitmap? bitmap = null;
        ImageDecodeInfo? info = null;
        string? notice = null;
        byte[] bytes = [];

        try
        {
            bytes = await ReadLimitedAsync(fileSystem, entry, (int)MaxImageBytes, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"读取图片失败: {entry.Path}", ex);
        }

        if (bytes.Length > 0)
            bitmap = await DecodeBytesAsync(bytes, result => info = result, cancellationToken).ConfigureAwait(true);

        if (bitmap is null && localPath is not null)
        {
            var decoded = await FfmpegTool.ExtractFrameAsync(localPath, ffmpegPath, null, cancellationToken)
                .ConfigureAwait(true);
            if (decoded is { Length: > 0 })
            {
                bitmap = DecodeWithAvalonia(decoded, out info);
                if (bitmap is not null)
                    notice = L10n.T("此图片由 ffmpeg 解码。");
            }
        }

        if (info is not null)
        {
            details.Add(new PreviewDetail(L10n.T("尺寸"), $"{info.Width} × {info.Height}"));
            if (!string.IsNullOrWhiteSpace(info.Format))
                details.Add(new PreviewDetail(L10n.T("格式"), info.Format.ToUpperInvariant()));
            if (info.BitDepth is > 0)
                details.Add(new PreviewDetail(L10n.T("位深"), $"{info.BitDepth} bit"));
            if (info.Frames is > 1)
                details.Add(new PreviewDetail(L10n.T("帧数"), info.Frames.Value.ToString()));
            if (info.Orientation is not null && info.Orientation != nameof(ImageOrientation.Normal))
                details.Add(new PreviewDetail(L10n.T("方向"), info.Orientation));
        }

        if (info?.Frames is > 1)
            notice = JoinNotice(notice, L10n.T("动图仅显示第一帧。"));

        if (bitmap is null)
        {
            return new PreviewContent(PreviewKind.Image,
                Notice: JoinNotice(notice, L10n.T("无法解码此图片；可能缺少对应的解码器。")),
                Details: details);
        }

        return new PreviewContent(PreviewKind.Image, Image: bitmap, Notice: notice, Details: details);
    }

    private async Task<PreviewContent> PreviewMediaAsync(FileEntry entry, List<PreviewDetail> details,
        string? localPath, string? ffmpegPath, bool video, CancellationToken cancellationToken)
    {
        if (localPath is null)
        {
            return new PreviewContent(video ? PreviewKind.Video : PreviewKind.Audio,
                Notice: L10n.T("远程媒体暂不支持 ffmpeg 预览。"), Details: details);
        }

        var probe = await FfmpegTool.ProbeAsync(localPath, ffmpegPath, cancellationToken).ConfigureAwait(true);
        details.AddRange(probe.Details);

        Bitmap? thumbnail = null;
        if (video)
        {
            var position = probe.Duration is { TotalSeconds: > 0 } duration
                ? Math.Clamp(duration.TotalSeconds * 0.1, 0.5, 30)
                : 1;
            var frame = await FfmpegTool.ExtractFrameAsync(localPath, ffmpegPath, position, cancellationToken)
                .ConfigureAwait(true);
            if (frame is { Length: > 0 })
            {
                thumbnail = DecodeWithAvalonia(frame, out _);
                if (thumbnail is not null)
                    details.Add(new PreviewDetail(L10n.T("缩略图"), L10n.T("ffmpeg 第 1 帧")));
            }
        }

        return new PreviewContent(video ? PreviewKind.Video : PreviewKind.Audio,
            Image: thumbnail,
            Notice: probe.Notice,
            Details: details);
    }

    private async Task<PreviewContent> PreviewPdfAsync(FileEntry entry, List<PreviewDetail> details,
        string? localPath, CancellationToken cancellationToken)
    {
        if (localPath is not null)
        {
            var page = await PdfPreviewTool.ExtractFirstPageAsync(localPath, cancellationToken).ConfigureAwait(true);
            if (page is { Length: > 0 })
            {
                var bitmap = await DecodeBytesAsync(page, _ => { }, cancellationToken).ConfigureAwait(true);
                if (bitmap is not null)
                {
                    return new PreviewContent(PreviewKind.Document, Image: bitmap,
                        Notice: L10n.T("PDF 首页由 pdftoppm 渲染。"), Details: details);
                }
            }
        }

        return new PreviewContent(PreviewKind.Document,
            Notice: localPath is null
                ? L10n.T("远程 PDF 暂不支持首页预览。")
                : L10n.T("未能渲染 PDF 首页；安装 poppler-utils（pdftoppm）后可预览。"),
            Details: details);
    }

    private async Task<PreviewContent> PreviewDocumentAsync(FileEntry entry, List<PreviewDetail> details,
        string? localPath, CancellationToken cancellationToken)
    {
        if (localPath is not null)
        {
            var thumbnail = await Task.Run(() => ReadDocumentThumbnail(localPath), cancellationToken)
                .ConfigureAwait(true);
            if (thumbnail is { Length: > 0 })
            {
                var bitmap = await DecodeBytesAsync(thumbnail, _ => { }, cancellationToken).ConfigureAwait(true);
                if (bitmap is not null)
                {
                    return new PreviewContent(PreviewKind.Document, Image: bitmap,
                        Notice: L10n.T("显示文档内嵌的缩略图。"), Details: details);
                }
            }
        }

        return new PreviewContent(PreviewKind.Document,
            Notice: localPath is null
                ? L10n.T("远程文档暂不支持缩略图预览。")
                : L10n.T("此文档格式暂无内置预览。"),
            Details: details);
    }

    private async Task<Bitmap?> DecodeBytesAsync(byte[] bytes, Action<ImageDecodeInfo?> setInfo,
        CancellationToken cancellationToken)
    {
        Bitmap? bitmap = null;
        var skia = await Task.Run(() => DecodeWithSkia(bytes), cancellationToken).ConfigureAwait(true);
        if (skia is not null)
        {
            bitmap = ToAvaloniaBitmap(skia.Bitmap);
            setInfo(skia.Info);
            skia.Bitmap.Dispose();
        }

        if (bitmap is null)
        {
            var simd = await Task.Run(() => DecodeWithSimd(bytes), cancellationToken).ConfigureAwait(true);
            if (simd is not null)
            {
                bitmap = ToAvaloniaBitmap(simd.Bitmap);
                setInfo(simd.Info);
                simd.Bitmap.Dispose();
            }
        }

        if (bitmap is null)
        {
            bitmap = DecodeWithAvalonia(bytes, out var info);
            setInfo(info);
        }

        return bitmap;
    }

    private async Task<PreviewContent> PreviewTextAsync(IFileSystem fileSystem, FileEntry entry,
        List<PreviewDetail> details, bool forceText, CancellationToken cancellationToken)
    {
        var bytes = await ReadLimitedAsync(fileSystem, entry, MaxTextBytes, cancellationToken).ConfigureAwait(true);
        var truncated = entry.Size > bytes.Length || bytes.Length >= MaxTextBytes;
        var read = DecodeText(bytes, forceText);

        details.Add(new PreviewDetail(L10n.T("编码"), read.EncodingName));
        if (read.LineCount > 0)
            details.Add(new PreviewDetail(L10n.T("行数"), read.LineCount.ToString()));

        if (!read.IsText)
        {
            return new PreviewContent(PreviewKind.Info,
                Text: BuildHexDump(bytes.AsSpan(0, Math.Min(bytes.Length, MaxHexBytes)).ToArray()),
                Notice: L10n.T("此文件看起来是二进制数据，以下显示文件头。"),
                Details: details);
        }

        var notice = truncated
            ? L10n.Format("文件较大，仅显示前 {0}。", Humanize.Size(bytes.Length))
            : null;

        return new PreviewContent(PreviewKind.Text, Text: read.Text, Notice: notice, Details: details);
    }

    private async Task<PreviewContent> PreviewFolderAsync(IFileSystem fileSystem, FileEntry entry,
        List<PreviewDetail> details, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await fileSystem.ListAsync(entry.Path, cancellationToken).ConfigureAwait(true);
            var ordered = entries.OrderBy(item => item, new FileEntryComparer(SortColumn.Name, false, true)).ToList();
            details.Add(new PreviewDetail(L10n.T("项目"), L10n.Format("{0} 项", ordered.Count)));

            var rows = ordered.Take(MaxRows).Select(ToRow).ToList();
            var notice = ordered.Count > MaxRows
                ? L10n.Format("仅显示前 {0} 项。", MaxRows)
                : null;
            return new PreviewContent(PreviewKind.Folder, Rows: rows, Notice: notice, Details: details);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"读取文件夹预览失败: {entry.Path}", ex);
            return new PreviewContent(PreviewKind.Folder,
                Notice: L10n.T("无法读取此文件夹。"), Details: details);
        }
    }

    private async Task<PreviewContent?> TryArchiveAsync(FileEntry entry, string? localPath,
        List<PreviewDetail> details, CancellationToken cancellationToken, bool zipOnly = false)
    {
        if (localPath is null)
        {
            return new PreviewContent(PreviewKind.Archive,
                Notice: L10n.T("远程压缩包暂不支持内容预览。"), Details: details);
        }

        try
        {
            return await Task.Run(() => zipOnly
                ? ReadZipArchive(localPath, entry, details)
                : ReadArchiveByExtension(localPath, entry, details), cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取压缩包预览失败: {entry.Path}", ex);
            return new PreviewContent(PreviewKind.Archive,
                Notice: L10n.T("无法读取此压缩包。"), Details: details);
        }
    }

    private static PreviewContent? ReadArchiveByExtension(string path, FileEntry entry,
        List<PreviewDetail> details)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".zip", StringComparison.Ordinal) ||
            lower.EndsWith(".jar", StringComparison.Ordinal) ||
            lower.EndsWith(".apk", StringComparison.Ordinal))
        {
            return ReadZipArchive(path, entry, details);
        }
        if (lower.EndsWith(".tar", StringComparison.Ordinal) ||
            lower.EndsWith(".tar.gz", StringComparison.Ordinal) ||
            lower.EndsWith(".tgz", StringComparison.Ordinal))
        {
            return ReadTarArchive(path, details);
        }
        return new PreviewContent(PreviewKind.Archive,
            Notice: L10n.T("此压缩格式暂不支持内容预览。"), Details: details);
    }

    private static PreviewContent ReadZipArchive(string path, FileEntry entry, List<PreviewDetail> details)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var total = archive.Entries.Count;
        details.Add(new PreviewDetail(L10n.T("条目"), total.ToString()));

        var rows = archive.Entries
            .Take(MaxRows)
            .Select(item => ToArchiveRow(item.FullName, item.Length, item.FullName.EndsWith('/')))
            .ToList();
        return new PreviewContent(PreviewKind.Archive, Rows: rows,
            Notice: total > MaxRows ? L10n.Format("仅显示前 {0} 项。", MaxRows) : null,
            Details: details);
    }

    private static PreviewContent ReadTarArchive(string path, List<PreviewDetail> details)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using Stream? gzip = path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : null;
        using var reader = new TarReader(gzip ?? file, leaveOpen: true);
        var rows = new List<PreviewRow>();
        var total = 0;
        TarEntry? item;
        while ((item = reader.GetNextEntry()) is not null)
        {
            total++;
            if (rows.Count < MaxRows)
            {
                rows.Add(ToArchiveRow(item.Name, item.Length,
                    item.EntryType is TarEntryType.Directory));
            }
        }

        details.Add(new PreviewDetail(L10n.T("条目"), total.ToString()));
        return new PreviewContent(PreviewKind.Archive, Rows: rows,
            Notice: total > MaxRows ? L10n.Format("仅显示前 {0} 项。", MaxRows) : null,
            Details: details);
    }

    private static PreviewRow ToArchiveRow(string name, long size, bool isDirectory)
    {
        var fileName = name.TrimEnd('/');
        var baseName = fileName.Length == 0 ? name : fileName[(fileName.LastIndexOf('/') + 1)..];
        var visual = FileIconCatalog.For(new FileEntry
        {
            Name = baseName,
            Path = name,
            Kind = isDirectory ? EntryKind.Directory : EntryKind.File,
        });
        return new PreviewRow(
            fileName.Length == 0 ? name : fileName,
            isDirectory ? L10n.T("文件夹") : Humanize.Size(size),
            isDirectory,
            visual.Geometry,
            visual.Brush);
    }

    private static PreviewRow ToRow(FileEntry entry)
    {
        var visual = FileIconCatalog.For(entry);
        return new PreviewRow(
            entry.Name,
            entry.IsDirectory ? L10n.T("文件夹") : Humanize.Size(entry.Size),
            entry.IsDirectory,
            visual.Geometry,
            visual.Brush);
    }

    private static List<PreviewDetail> BuildBaseDetails(IFileSystem fileSystem, FileEntry entry)
    {
        var details = new List<PreviewDetail>
        {
            new(L10n.T("类型"), entry.TypeDescription),
            new(L10n.T("大小"), entry.IsDirectory ? "—" : Humanize.Size(entry.Size)),
            new(L10n.T("修改时间"), Humanize.DateTimeFull(entry.Modified)),
        };

        if (entry.Created is { } created)
            details.Add(new PreviewDetail(L10n.T("创建时间"), Humanize.DateTimeFull(created)));
        if (entry.UnixMode is { } mode)
            details.Add(new PreviewDetail(L10n.T("权限"), $"{Humanize.Permissions(mode)} ({Humanize.OctalPermissions(mode)})"));
        if (!string.IsNullOrWhiteSpace(entry.Owner) || !string.IsNullOrWhiteSpace(entry.Group))
            details.Add(new PreviewDetail(L10n.T("所有者"), $"{entry.Owner ?? "—"} / {entry.Group ?? "—"}"));
        if (entry.IsSymlink && !string.IsNullOrWhiteSpace(entry.LinkTarget))
            details.Add(new PreviewDetail(L10n.T("链接目标"), entry.LinkTarget!));
        details.Add(new PreviewDetail(L10n.T("路径"), fileSystem.GetDisplayPath(entry.Path)));
        return details;
    }

    private static async Task<byte[]> ReadPrefixAsync(IFileSystem fileSystem, FileEntry entry, int count,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await fileSystem.OpenReadAsync(entry.Path, cancellationToken).ConfigureAwait(true);
            var buffer = new byte[count];
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(true);
            return buffer[..read];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"读取文件头失败: {entry.Path}", ex);
            return [];
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(IFileSystem fileSystem, FileEntry entry, int limit,
        CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenReadAsync(entry.Path, cancellationToken).ConfigureAwait(true);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (memory.Length < limit)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - (int)memory.Length)),
                cancellationToken).ConfigureAwait(true);
            if (read == 0)
                break;
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static string? LocalPathOf(FileEntry entry)
    {
        try
        {
            return Path.IsPathRooted(entry.Path) && File.Exists(entry.Path) ? entry.Path : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsImageExtension(string extension)
        => MimeTypes.IsImage(extension) || extension is
            "tif" or "tiff" or "heic" or "heif" or "avif" or "psd" or "raw" or "dng" or
            "cr2" or "cr3" or "nef" or "arw" or "orf" or "rw2" or "jxl" or "jp2" or "j2k" or
            "jpx" or "qoi" or "pnm" or "pbm" or "pgm" or "ppm" or "pam" or "pfm";

    private static bool IsVideoExtension(string extension) => extension is
        "mp4" or "mkv" or "webm" or "avi" or "mov" or "wmv" or "flv" or "m4v" or "mpg" or "mpeg";

    private static bool IsAudioExtension(string extension) => extension is
        "mp3" or "flac" or "ogg" or "oga" or "opus" or "wav" or "m4a" or "aac" or "wma" or "mid" or "midi";

    private static bool IsOfficeExtension(string extension) => extension is
        "doc" or "docx" or "odt" or "rtf" or "epub" or "xls" or "xlsx" or "ods" or
        "ppt" or "pptx" or "odp";

    private static bool IsImageHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
            return false;
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            return true;
        if (header[0] == 0xFF && header[1] == 0xD8)
            return true;
        if (header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'8')
            return true;
        if (header[0] == (byte)'B' && header[1] == (byte)'M')
            return true;
        if (header[0] == (byte)'I' && header[1] == (byte)'I' && header[2] == 0x2A && header[3] == 0x00)
            return true;
        if (header[0] == (byte)'M' && header[1] == (byte)'M' && header[2] == 0x00 && header[3] == 0x2A)
            return true;
        if (header.Length >= 12 && header[0] == (byte)'R' && header[1] == (byte)'I' &&
            header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            return true;
        if (header[0] == (byte)'q' && header[1] == (byte)'o' && header[2] == (byte)'i' && header[3] == (byte)'f')
            return true;
        if (header.Length >= 8 && header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x00 &&
            header[3] == 0x0C && header[4] == 0x6A && header[5] == 0x50 && header[6] == 0x20 && header[7] == 0x20)
            return true;
        if (header.Length >= 8 && header[0] == 0x97 && header[1] == (byte)'J' && header[2] == (byte)'B' &&
            header[3] == (byte)'2' && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
            return true;
        if (header.Length >= 4 && header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0x00)
            return true;
        if (header.Length >= 12 && header[4] == (byte)'f' && header[5] == (byte)'t' &&
            header[6] == (byte)'y' && header[7] == (byte)'p')
        {
            var brand = System.Text.Encoding.ASCII.GetString(header.Slice(8, 4));
            if (brand is "heic" or "heix" or "hevc" or "hevx" or "mif1" or "msf1" or "avif" or "avis")
                return true;
        }
        if (header.Length >= 4 && header[0] == 0x38 && header[1] == 0x42 && header[2] == 0x50 && header[3] == 0x53)
            return true;
        if (header.Length >= 2 && header[0] == 0xFF && header[1] == 0x0A)
            return true;
        return header.Length >= 2 && header[0] == (byte)'P' && header[1] is >= (byte)'1' and <= (byte)'6';
    }

    private static bool IsZipHeader(ReadOnlySpan<byte> header)
        => header.Length >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K' &&
           header[2] is 3 or 5 or 7 && header[3] is 4 or 6 or 8;

    private static bool IsPdfHeader(ReadOnlySpan<byte> header)
        => header.Length >= 4 && header[0] == (byte)'%' && header[1] == (byte)'P' &&
           header[2] == (byte)'D' && header[3] == (byte)'F';

    private static bool IsTextHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length == 0)
            return true;
        if (header.Length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            return true;
        if (header.Length >= 4 && ((header[0] == 0xFF && header[1] == 0xFE && header[2] == 0x00 && header[3] == 0x00) ||
                                    (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0xFE && header[3] == 0xFF)))
        {
            return true;
        }
        if (header.Length >= 2 && ((header[0] == 0xFF && header[1] == 0xFE) ||
                                   (header[0] == 0xFE && header[1] == 0xFF)))
        {
            return true;
        }

        var controls = 0;
        foreach (var value in header)
        {
            if (value == 0)
                return false;
            if (value < 0x09 || (value > 0x0D && value < 0x20))
                controls++;
        }
        return controls * 20 <= header.Length && TryDecodeUtf8(header.ToArray(), true, out _);
    }

    private TextDecodeResult DecodeText(byte[] bytes, bool forceText)
    {
        if (bytes.Length == 0)
            return new TextDecodeResult(string.Empty, "UTF-8", true, 0);

        if (!forceText && !IsTextHeader(bytes.AsSpan(0, Math.Min(bytes.Length, 8192))))
            return new TextDecodeResult(string.Empty, L10n.T("二进制"), false, 0);

        string text;
        string encoding;
        var offset = 0;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            offset = 3;
            text = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
            encoding = "UTF-8 (BOM)";
        }
        else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE &&
                 bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            offset = 4;
            text = Encoding.UTF32.GetString(bytes, offset, bytes.Length - offset);
            encoding = "UTF-32 LE";
        }
        else if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 &&
                 bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            offset = 4;
            text = new UTF32Encoding(true, false).GetString(bytes, offset, bytes.Length - offset);
            encoding = "UTF-32 BE";
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            offset = 2;
            text = Encoding.Unicode.GetString(bytes, offset, bytes.Length - offset);
            encoding = "UTF-16 LE";
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            offset = 2;
            text = Encoding.BigEndianUnicode.GetString(bytes, offset, bytes.Length - offset);
            encoding = "UTF-16 BE";
        }
        else if (TryDecodeUtf8(bytes, false, out text))
        {
            encoding = "UTF-8";
        }
        else if (TryDecodeUtf8(bytes, true, out text))
        {
            encoding = "UTF-8";
        }
        else if (TryDecodeGb18030(bytes, out text))
        {
            encoding = "GB18030";
        }
        else
        {
            text = Encoding.Latin1.GetString(bytes);
            encoding = "Latin-1";
        }

        return new TextDecodeResult(text, encoding, true, CountLines(text));
    }

    private static bool TryDecodeUtf8(byte[] bytes, bool allowTruncatedTail, out string text)
    {
        text = string.Empty;
        var length = bytes.Length;
        var attempts = allowTruncatedTail ? 4 : 1;
        for (var trim = 0; trim < attempts && length - trim >= 0; trim++)
        {
            try
            {
                text = StrictUtf8.GetString(bytes, 0, length - trim);
                return true;
            }
            catch (DecoderFallbackException)
            {
                // A read boundary can cut a multi-byte sequence; try one byte shorter.
            }
        }
        return false;
    }

    private static bool TryDecodeGb18030(byte[] bytes, out string text)
    {
        text = string.Empty;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            text = encoding.GetString(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
            return 0;
        var lines = 1;
        foreach (var character in text)
        {
            if (character == '\n')
                lines++;
        }
        return lines;
    }

    private static byte[]? ReadDocumentThumbnail(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            string[] candidates =
            [
                "docProps/thumbnail.jpeg",
                "docProps/thumbnail.jpg",
                "docProps/thumbnail.png",
                "Thumbnails/thumbnail.png",
                "Thumbnails/thumbnail.jpeg",
                "thumbnail.png",
                "thumbnail.jpeg",
                "thumbnail.jpg",
            ];

            var item = archive.Entries.FirstOrDefault(entry =>
                candidates.Contains(entry.FullName, StringComparer.OrdinalIgnoreCase));
            if (item is null || item.Length is <= 0 or > 16 * 1024 * 1024)
                return null;

            using var source = item.Open();
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static string BuildHexDump(ReadOnlySpan<byte> bytes)
    {
        bytes = bytes[..Math.Min(bytes.Length, MaxHexBytes)];
        var builder = new StringBuilder(bytes.Length * 4);
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            builder.Append(offset.ToString("x8")).Append("  ");
            var count = Math.Min(16, bytes.Length - offset);
            for (var index = 0; index < 16; index++)
            {
                if (index < count)
                    builder.Append(bytes[offset + index].ToString("x2")).Append(' ');
                else
                    builder.Append("   ");
                if (index == 7)
                    builder.Append(' ');
            }
            builder.Append("  ");
            for (var index = 0; index < count; index++)
            {
                var value = bytes[offset + index];
                builder.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string JoinNotice(string? first, string second)
        => string.IsNullOrWhiteSpace(first) ? second : $"{first} {second}";

    private static SKBitmap? DecodeSkiaBitmap(byte[] bytes, out ImageDecodeInfo? info)
    {
        info = null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
                return null;

            var source = codec.Info;
            if (source.Width <= 0 || source.Height <= 0)
                return null;

            var scale = Math.Min(1f, (float)MaxImageDimension / Math.Max(source.Width, source.Height));
            var scaled = codec.GetScaledDimensions(scale);
            var targetWidth = Math.Max(1, scaled.Width);
            var targetHeight = Math.Max(1, scaled.Height);
            var decodeInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888,
                SKAlphaType.Premul);
            var decoded = new SKBitmap(decodeInfo);
            var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                decoded.Dispose();
                return null;
            }

            var oriented = Orient(decoded, codec.EncodedOrigin);
            decoded.Dispose();
            var fitted = ScaleToFit(oriented, MaxImageDimension);
            oriented.Dispose();

            info = new ImageDecodeInfo(source.Width, source.Height, codec.EncodedFormat.ToString(),
                codec.FrameCount, codec.EncodedOrigin == SKEncodedOrigin.TopLeft ? null : codec.EncodedOrigin.ToString());
            return fitted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Skia 图片解码失败", ex);
            return null;
        }
    }

    private static SkiaDecodeResult? DecodeWithSkia(byte[] bytes)
    {
        var bitmap = DecodeSkiaBitmap(bytes, out var info);
        return bitmap is null ? null : new SkiaDecodeResult(bitmap, info);
    }

    private static SimdDecodeResult? DecodeWithSimd(byte[] bytes)
    {
        try
        {
            if (!SimdCodecsFacade.TryReadInfo(bytes, out var info) ||
                !SimdCodecsFacade.IsDecodable(info.Format) ||
                info.Width <= 0 || info.Height <= 0)
            {
                return null;
            }

            var stride = checked(info.Width * 4);
            var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * info.Height));
            if (!SimdCodecsFacade.TryDecodeInto(bytes, pixels, stride, ImagePixelFormat.Bgra32))
                return null;

            var sourceInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888,
                SKAlphaType.Unpremul);
            var source = new SKBitmap(sourceInfo);
            Marshal.Copy(pixels, 0, source.GetPixels(), pixels.Length);
            var oriented = Orient(source, ToSkiaOrigin(info.Orientation));
            source.Dispose();
            var fitted = ScaleToFit(oriented, MaxImageDimension);
            oriented.Dispose();

            return new SimdDecodeResult(fitted, new ImageDecodeInfo(
                info.Width, info.Height, info.Format.ToString(), info.FrameCount,
                info.Orientation == ImageOrientation.Normal ? null : info.Orientation.ToString(),
                info.BitDepth));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("SimdCodecs 图片解码失败", ex);
            return null;
        }
    }

    private static Bitmap? DecodeWithAvalonia(byte[] bytes, out ImageDecodeInfo? info)
    {
        info = null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = Bitmap.DecodeToWidth(stream, MaxImageDimension);
            if (bitmap is null)
                return null;
            info = new ImageDecodeInfo(bitmap.PixelSize.Width, bitmap.PixelSize.Height, "Avalonia",
                1, null);
            return bitmap;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Avalonia 图片解码失败", ex);
            return null;
        }
    }

    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or
            SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;
        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888,
            SKAlphaType.Premul));

        using var canvas = new SKCanvas(target);
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, source.Width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, source.Width, 0, -1, source.Height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, source.Height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, source.Height, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, source.Height, -1, 0, source.Width, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, source.Width, 0, 0, 1),
            _ => SKMatrix.CreateIdentity(),
        };
        canvas.SetMatrix(matrix);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(source, 0, 0, paint);
        return target;
    }

    private static SKBitmap ScaleToFit(SKBitmap source, int maxDimension)
    {
        if (source.Width <= maxDimension && source.Height <= maxDimension)
            return source.Copy();

        var scale = (double)maxDimension / Math.Max(source.Width, source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888,
            SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        canvas.Scale(width / (float)source.Width, height / (float)source.Height);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(source, 0, 0, paint);
        return target;
    }

    private static SKEncodedOrigin ToSkiaOrigin(ImageOrientation orientation) => orientation switch
    {
        ImageOrientation.FlipHorizontal => SKEncodedOrigin.TopRight,
        ImageOrientation.Rotate180 => SKEncodedOrigin.BottomRight,
        ImageOrientation.FlipVertical => SKEncodedOrigin.BottomLeft,
        ImageOrientation.Transpose => SKEncodedOrigin.LeftTop,
        ImageOrientation.Rotate90 => SKEncodedOrigin.RightTop,
        ImageOrientation.Transverse => SKEncodedOrigin.RightBottom,
        ImageOrientation.Rotate270 => SKEncodedOrigin.LeftBottom,
        _ => SKEncodedOrigin.TopLeft,
    };

    private static Bitmap ToAvaloniaBitmap(SKBitmap bitmap)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => ToAvaloniaBitmap(bitmap));

        var result = new WriteableBitmap(
            new PixelSize(bitmap.Width, bitmap.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using (var frame = result.Lock())
        {
            var source = bitmap.GetPixels();
            var rowBytes = Math.Min(bitmap.RowBytes, frame.RowBytes);
            var row = new byte[rowBytes];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(source, y * bitmap.RowBytes), row, 0, rowBytes);
                Marshal.Copy(row, 0, IntPtr.Add(frame.Address, y * frame.RowBytes), rowBytes);
            }
        }
        return result;
    }

    private sealed record ImageDecodeInfo(int Width, int Height, string? Format, int? Frames,
        string? Orientation, int? BitDepth = null);

    private sealed record SkiaDecodeResult(SKBitmap Bitmap, ImageDecodeInfo? Info);

    private sealed record SimdDecodeResult(SKBitmap Bitmap, ImageDecodeInfo Info);

    private sealed record TextDecodeResult(string Text, string EncodingName, bool IsText, int LineCount);
}
