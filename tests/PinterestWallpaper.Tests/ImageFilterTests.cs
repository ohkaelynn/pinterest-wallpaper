using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Media;
using SkiaSharp;
using Xunit;

namespace PinterestWallpaper.Tests;

public sealed class ImageFilterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pinwall-img-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidateDimensions_EnforcesResolutionOrientationAndAspect()
    {
        var filter = new FilterConfig
        {
            MinWidth = 1920,
            MinHeight = 1080,
            Orientation = ImageOrientation.Landscape,
            AspectRatioMin = 1.3,
            AspectRatioMax = 3.6
        };

        Assert.True(ImageFilter.ValidateDimensions(1920, 1080, filter).IsValid);
        Assert.True(ImageFilter.ValidateDimensions(3440, 1440, filter).IsValid);
        Assert.False(ImageFilter.ValidateDimensions(1280, 720, filter).IsValid);
        Assert.False(ImageFilter.ValidateDimensions(1920, 2400, filter).IsValid); // Portrait
    }

    [Fact]
    public void ValidateFile_DecodesImageAndDetectsNearDuplicateHash()
    {
        Directory.CreateDirectory(_tempDir);
        var img1 = Path.Combine(_tempDir, "a.png");
        var img2 = Path.Combine(_tempDir, "b.png");

        CreateGradientPng(img1, 1920, 1080);
        CreateGradientPng(img2, 1920, 1080);

        var filter = new FilterConfig();
        var first = ImageFilter.ValidateFile(img1, filter);
        Assert.True(first.IsValid);
        Assert.Equal(1920, first.Width);
        Assert.Equal(1080, first.Height);

        var duplicate = ImageFilter.ValidateFile(img2, filter, [first.PerceptualHash]);
        Assert.False(duplicate.IsValid);
        Assert.Contains("Duplicate", duplicate.RejectionReason);
    }

    private static void CreateGradientPng(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, height),
                [SKColors.DarkSlateBlue, SKColors.Coral],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, width, height, paint);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
