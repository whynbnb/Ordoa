using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

/// <summary>
/// Optional ffmpeg/ffprobe integration. Ordoa never bundles these tools: they are
/// discovered from PATH, environment variables, or the path configured in Settings.
/// </summary>
public static class FfmpegTool
{
    public sealed record ProbeResult(
        string? Notice,
        IReadOnlyList<PreviewDetail> Details,
        bool IsVideo,
        bool IsAudio,
        TimeSpan? Duration);

    public static ProbeResult ProbeUnavailable(string? configured)
    {
        var (ffmpeg, ffprobe) = Resolve(configured);
        if (ffmpeg is null && ffprobe is null)
        {
            return new ProbeResult(
                L10n.T("未找到 ffmpeg/ffprobe。请安装 ffmpeg，或在设置中指定它的路径。"),
                [], false, false, null);
        }

        return new ProbeResult(
            L10n.T("无法读取媒体信息。"),
            [], false, false, null);
    }

    public static async Task<ProbeResult> ProbeAsync(string path, string? configured,
        CancellationToken cancellationToken)
    {
        var (ffmpeg, ffprobe) = Resolve(configured);
        if (ffprobe is null)
        {
            if (ffmpeg is null)
            {
                return new ProbeResult(
                    L10n.T("未找到 ffmpeg/ffprobe。请安装 ffmpeg，或在设置中指定它的路径。"),
                    [], false, false, null);
            }

            return await ProbeWithFfmpegAsync(ffmpeg, path, cancellationToken).ConfigureAwait(true);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var process = StartProcess(ffprobe,
            [
                "-v", "error",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                path,
            ]);

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await WaitForExitOrKillAsync(process, timeout.Token).ConfigureAwait(true);
            var json = await stdout.ConfigureAwait(true);
            _ = await stderr.ConfigureAwait(true);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                return ProbeUnavailable(configured);

            return ParseProbeJson(json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(L10n.T("读取媒体信息超时。"), [], false, false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"ffprobe 读取失败: {path}", ex);
            return ProbeUnavailable(configured);
        }
    }

    /// <summary>Extract one PNG frame (video thumbnail or a fallback image decoder).</summary>
    public static async Task<byte[]?> ExtractFrameAsync(string path, string? configured, double? seconds,
        CancellationToken cancellationToken)
    {
        var (ffmpeg, _) = Resolve(configured);
        if (ffmpeg is null)
            return null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));

            var args = new List<string>
            {
                "-hide_banner",
                "-loglevel", "error",
                "-nostdin",
            };
            if (seconds is { } position)
                args.AddRange(["-ss", position.ToString("0.###", CultureInfo.InvariantCulture)]);
            args.AddRange(
            [
                "-i", path,
                "-frames:v", "1",
                "-vf", "scale=w='min(2048,iw)':h=-2",
                "-f", "image2pipe",
                "-vcodec", "png",
                "pipe:1",
            ]);

            using var process = StartProcess(ffmpeg, args);
            var stdout = ReadAllBytesLimitedAsync(process.StandardOutput.BaseStream, 64L * 1024 * 1024, timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await WaitForExitOrKillAsync(process, timeout.Token).ConfigureAwait(true);
            var bytes = await stdout.ConfigureAwait(true);
            _ = await stderr.ConfigureAwait(true);

            return process.ExitCode == 0 && bytes.Length > 0 ? bytes : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"ffmpeg 生成预览失败: {path}", ex);
            return null;
        }
    }

    private static async Task<ProbeResult> ProbeWithFfmpegAsync(string ffmpeg, string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var process = StartProcess(ffmpeg,
            [
                "-hide_banner",
                "-nostdin",
                "-i", path,
            ]);

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await WaitForExitOrKillAsync(process, timeout.Token).ConfigureAwait(true);
            _ = await stdout.ConfigureAwait(true);
            var text = await stderr.ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(text))
                return new ProbeResult(L10n.T("无法读取媒体信息。"), [], false, false, null);

            var details = new List<PreviewDetail>();
            var video = false;
            var audio = false;
            TimeSpan? duration = null;

            foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("Duration:", StringComparison.Ordinal))
                {
                    var value = line["Duration:".Length..].Split(',')[0].Trim();
                    if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
                    {
                        duration = parsed;
                        details.Add(new PreviewDetail(L10n.T("时长"), FormatDuration(parsed)));
                    }
                }

                if (line.Contains(" Video: ", StringComparison.Ordinal))
                {
                    video = true;
                    var after = line[(line.IndexOf(" Video: ", StringComparison.Ordinal) + 8)..];
                    var parts = after.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length > 0)
                        details.Add(new PreviewDetail(L10n.T("视频编码"), parts[0]));
                    var resolution = parts.FirstOrDefault(p => p.Contains('x', StringComparison.Ordinal) &&
                                                               p.All(c => char.IsDigit(c) || c == 'x' || c == ' '));
                    if (!string.IsNullOrEmpty(resolution))
                        details.Add(new PreviewDetail(L10n.T("分辨率"), resolution));
                }
                else if (line.Contains(" Audio: ", StringComparison.Ordinal))
                {
                    audio = true;
                    var after = line[(line.IndexOf(" Audio: ", StringComparison.Ordinal) + 8)..];
                    var parts = after.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length > 0)
                        details.Add(new PreviewDetail(L10n.T("音频编码"), parts[0]));
                    var sampleRate = parts.FirstOrDefault(p => p.EndsWith(" Hz", StringComparison.Ordinal));
                    if (!string.IsNullOrEmpty(sampleRate))
                        details.Add(new PreviewDetail(L10n.T("采样率"), sampleRate));
                }
            }

            return new ProbeResult(details.Count == 0 ? L10n.T("无法读取媒体信息。") : null,
                details, video, audio, duration);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(L10n.T("读取媒体信息超时。"), [], false, false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"ffmpeg 读取媒体信息失败: {path}", ex);
            return new ProbeResult(L10n.T("无法读取媒体信息。"), [], false, false, null);
        }
    }

    private static ProbeResult ParseProbeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var details = new List<PreviewDetail>();
        var video = false;
        var audio = false;
        TimeSpan? duration = null;

        string? Text(JsonElement parent, string property)
            => parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        if (root.TryGetProperty("format", out var format))
        {
            var formatName = Text(format, "format_long_name") ?? Text(format, "format_name");
            if (!string.IsNullOrWhiteSpace(formatName))
                details.Add(new PreviewDetail(L10n.T("容器"), formatName));

            if (Text(format, "duration") is { } durationText &&
                double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                seconds > 0)
            {
                duration = TimeSpan.FromSeconds(seconds);
                details.Add(new PreviewDetail(L10n.T("时长"), FormatDuration(duration.Value)));
            }

            if (Text(format, "bit_rate") is { } bitRateText &&
                long.TryParse(bitRateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitRate))
            {
                details.Add(new PreviewDetail(L10n.T("总码率"), FormatBitRate(bitRate)));
            }

            if (format.TryGetProperty("tags", out var tags))
            {
                AddTag(details, tags, "title", L10n.T("标题"));
                AddTag(details, tags, "artist", L10n.T("艺术家"));
                AddTag(details, tags, "album", L10n.T("专辑"));
                AddTag(details, tags, "date", L10n.T("年份"));
                AddTag(details, tags, "track", L10n.T("轨道"));
            }
        }

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = Text(stream, "codec_type");
                if (type == "video" && !video)
                {
                    video = true;
                    var codec = Text(stream, "codec_long_name") ?? Text(stream, "codec_name");
                    if (!string.IsNullOrWhiteSpace(codec))
                        details.Add(new PreviewDetail(L10n.T("视频编码"), codec));

                    var width = stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi) ? wi : 0;
                    var height = stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi) ? hi : 0;
                    if (width > 0 && height > 0)
                        details.Add(new PreviewDetail(L10n.T("分辨率"), $"{width} × {height}"));

                    var frameRateText = Text(stream, "avg_frame_rate") ?? Text(stream, "r_frame_rate");
                    if (TryParseFraction(frameRateText, out var frameRate) && frameRate > 0)
                        details.Add(new PreviewDetail(L10n.T("帧率"), $"{frameRate:0.##} fps"));

                    if (Text(stream, "bit_rate") is { } videoBitRate &&
                        long.TryParse(videoBitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vb))
                    {
                        details.Add(new PreviewDetail(L10n.T("视频码率"), FormatBitRate(vb)));
                    }
                }
                else if (type == "audio" && !audio)
                {
                    audio = true;
                    var codec = Text(stream, "codec_long_name") ?? Text(stream, "codec_name");
                    if (!string.IsNullOrWhiteSpace(codec))
                        details.Add(new PreviewDetail(L10n.T("音频编码"), codec));

                    if (Text(stream, "sample_rate") is { } sampleRate &&
                        int.TryParse(sampleRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sr) &&
                        sr > 0)
                    {
                        details.Add(new PreviewDetail(L10n.T("采样率"), $"{sr / 1000.0:0.#} kHz"));
                    }

                    if (stream.TryGetProperty("channels", out var channels) && channels.TryGetInt32(out var ch) && ch > 0)
                        details.Add(new PreviewDetail(L10n.T("声道"), ch == 1 ? L10n.T("单声道") : ch == 2 ? L10n.T("立体声") : $"{ch}"));
                }
            }
        }

        return new ProbeResult(null, details, video, audio, duration);
    }

    private static void AddTag(List<PreviewDetail> details, JsonElement tags, string property, string label)
    {
        if (tags.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text)
        {
            details.Add(new PreviewDetail(label, text));
        }
    }

    private static bool TryParseFraction(string? value, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split('/', 2);
        if (parts.Length == 1)
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator > 0)
        {
            result = numerator / denominator;
            return true;
        }
        return false;
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
            return value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
        return value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatBitRate(long bitsPerSecond)
    {
        if (bitsPerSecond >= 1_000_000)
            return $"{bitsPerSecond / 1_000_000.0:0.0} Mbps";
        if (bitsPerSecond >= 1_000)
            return $"{bitsPerSecond / 1_000.0:0} kbps";
        return $"{bitsPerSecond} bps";
    }

    private static async Task WaitForExitOrKillAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }
    }

    private static Process StartProcess(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"无法启动 {executable}");
        return process;
    }

    private static async Task<byte[]> ReadAllBytesLimitedAsync(Stream stream, long limit,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (memory.Length < limit)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - memory.Length)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static (string? Ffmpeg, string? Ffprobe) Resolve(string? configured)
    {
        string? ffmpeg = null;
        string? ffprobe = null;

        var explicitPath = FirstNonEmpty(configured,
            Environment.GetEnvironmentVariable("ORDOA_FFMPEG"),
            Environment.GetEnvironmentVariable("FFMPEG_PATH"),
            Environment.GetEnvironmentVariable("FFMPEG"));
        var explicitProbe = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ORDOA_FFPROBE"),
            Environment.GetEnvironmentVariable("FFPROBE_PATH"),
            Environment.GetEnvironmentVariable("FFPROBE"));

        if (explicitPath is not null)
        {
            var resolved = ResolveConfigured(explicitPath);
            if (resolved is not null)
            {
                if (IsProbeName(resolved))
                    ffprobe = resolved;
                else
                    ffmpeg = resolved;
            }
        }

        if (explicitProbe is not null)
        {
            var resolved = ResolveConfigured(explicitProbe);
            if (resolved is not null)
                ffprobe ??= resolved;
        }

        if (ffmpeg is not null)
            ffprobe ??= FindSibling(ffmpeg, "ffprobe");
        if (ffprobe is not null)
            ffmpeg ??= FindSibling(ffprobe, "ffmpeg");

        ffmpeg ??= FindExecutable("ffmpeg");
        ffprobe ??= FindExecutable("ffprobe");

        return (ffmpeg, ffprobe);
    }

    private static string? ResolveConfigured(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return null;
        if (value.StartsWith('~'))
            value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                value.TrimStart('~').TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (Directory.Exists(value))
        {
            return FindExecutable(Path.Combine(value, "ffmpeg"))
                   ?? FindExecutable(Path.Combine(value, "ffprobe"));
        }
        return FindExecutable(value);
    }

    private static string? FindSibling(string executable, string sibling)
    {
        var directory = Path.GetDirectoryName(executable);
        if (string.IsNullOrEmpty(directory))
            return null;
        return FindExecutable(Path.Combine(directory, sibling));
    }

    private static bool IsProbeName(string value)
        => Path.GetFileName(value).StartsWith("ffprobe", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? FindExecutable(string name)
    {
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(name) ? name : null;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        var extensions = OperatingSystem.IsWindows()
            ? new[] { string.Empty, ".exe", ".cmd", ".bat" }
            : new[] { string.Empty };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory, name + extension);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { /* ignore malformed PATH entry */ }
            }
        }
        return null;
    }
}
