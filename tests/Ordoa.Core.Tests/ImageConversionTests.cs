using Ordoa.Core.Services;
using SkiaSharp;

namespace Ordoa.Core.Tests;

public sealed class ImageConversionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ordoa-images-" + Guid.NewGuid().ToString("N"));

    public ImageConversionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ConvertsAndResizesImage()
    {
        var source = Path.Combine(_root, "source.png");
        using (var bitmap = new SKBitmap(100, 50))
        {
            bitmap.Erase(SKColors.CornflowerBlue);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            await using var stream = File.Create(source);
            data.SaveTo(stream);
        }

        var destination = Path.Combine(_root, "output.jpg");
        await ImageConversionService.ConvertAsync(source, destination,
            new ImageConversionOptions(ImageTargetFormat.Jpeg, 80, 20));

        Assert.True(File.Exists(destination));
        using var decoded = SKBitmap.Decode(destination);
        Assert.NotNull(decoded);
        Assert.Equal(20, decoded.Width);
        Assert.Equal(10, decoded.Height);
    }

    [Fact]
    public void BuildOutputPathAvoidsOverwritingSource()
    {
        var source = Path.Combine(_root, "photo.png");
        File.WriteAllBytes(source, []);

        var path = ImageConversionService.BuildOutputPath(source, _root, ImageTargetFormat.Png, "");

        Assert.NotEqual(source, path);
        Assert.Contains("(1)", path);
    }
}
