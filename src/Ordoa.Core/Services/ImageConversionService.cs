using Ordoa.Core.Localization;
using SkiaSharp;

namespace Ordoa.Core.Services;

public enum ImageTargetFormat
{
    Jpeg,
    Png,
    WebP,
}

public sealed record ImageConversionOptions(
    ImageTargetFormat Format,
    int Quality = 85,
    int MaxDimension = 0,
    string Suffix = "");

public static class ImageConversionService
{
    public static string ExtensionFor(ImageTargetFormat format) => format switch
    {
        ImageTargetFormat.Png => ".png",
        ImageTargetFormat.WebP => ".webp",
        _ => ".jpg",
    };

    public static string BuildOutputPath(string sourcePath, string outputDirectory, ImageTargetFormat format,
        string suffix)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath) + (suffix ?? string.Empty) +
                   ExtensionFor(format);
        var candidate = Path.Combine(outputDirectory, name);
        if (!string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(sourcePath),
                StringComparison.Ordinal) && !File.Exists(candidate))
            return candidate;

        for (var i = 1; i < 10_000; i++)
        {
            candidate = Path.Combine(outputDirectory,
                $"{Path.GetFileNameWithoutExtension(sourcePath)}{suffix} ({i}){ExtensionFor(format)}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(outputDirectory,
            $"{Path.GetFileNameWithoutExtension(sourcePath)}{suffix}-{Guid.NewGuid():N}{ExtensionFor(format)}");
    }

    public static Task<string> ConvertAsync(string sourcePath, string destinationPath,
        ImageConversionOptions options, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = new SKFileStream(sourcePath);
            using var codec = SKCodec.Create(input)
                ?? throw new InvalidOperationException(L10n.T("无法读取图片"));
            using var bitmap = new SKBitmap(codec.Info);
            var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidOperationException(L10n.T("无法解码图片"));

            var maxDimension = Math.Max(0, options.MaxDimension);
            SKBitmap? resized = null;
            try
            {
                var scale = maxDimension > 0
                    ? Math.Min(1d, maxDimension / (double)Math.Max(bitmap.Width, bitmap.Height))
                    : 1d;
                var source = bitmap;
                if (scale < 1d)
                {
                    var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
                    var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
                    resized = bitmap.Resize(new SKImageInfo(width, height, bitmap.ColorType, bitmap.AlphaType),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    if (resized is not null)
                        source = resized;
                }

                using var image = SKImage.FromBitmap(source);
                using var data = image.Encode(ToFormat(options.Format), Math.Clamp(options.Quality, 1, 100));
                if (data is null)
                    throw new InvalidOperationException(L10n.T("无法编码图片"));

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
                    FileShare.None);
                data.SaveTo(output);
                return destinationPath;
            }
            finally
            {
                resized?.Dispose();
            }
        }, cancellationToken);
    }

    private static SKEncodedImageFormat ToFormat(ImageTargetFormat format) => format switch
    {
        ImageTargetFormat.Png => SKEncodedImageFormat.Png,
        ImageTargetFormat.WebP => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Jpeg,
    };
}
