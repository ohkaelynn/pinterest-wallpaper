using System.Numerics;
using PinterestWallpaper.Core.Config;
using SkiaSharp;

namespace PinterestWallpaper.Core.Media;

public sealed record ImageCheckResult(
    bool IsValid,
    string? RejectionReason,
    int Width = 0,
    int Height = 0,
    ulong PerceptualHash = 0);

public static class ImageFilter
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp"
    };

    public static bool HasSupportedExtension(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    public static ImageCheckResult ValidateFile(
        string filePath,
        FilterConfig filter,
        IEnumerable<ulong>? existingHashes = null,
        int maxDuplicateHammingDistance = 6)
    {
        if (!File.Exists(filePath))
        {
            return new ImageCheckResult(false, "File does not exist.");
        }

        if (!HasSupportedExtension(filePath))
        {
            return new ImageCheckResult(false, "Unsupported image extension.");
        }

        // Fast header-only dimension check before decoding full pixels.
        using (var codec = SKCodec.Create(filePath))
        {
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                return new ImageCheckResult(false, "Could not decode image.");
            }

            var headerCheck = ValidateDimensions(codec.Info.Width, codec.Info.Height, filter);
            if (!headerCheck.IsValid)
            {
                return headerCheck;
            }
        }

        using var bitmap = SKBitmap.Decode(filePath);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return new ImageCheckResult(false, "Could not decode image.");
        }

        var hash = ComputeDifferenceHash(bitmap);
        if (filter.DeduplicateSimilar && existingHashes is not null)
        {
            foreach (var existing in existingHashes)
            {
                if (HammingDistance(hash, existing) <= maxDuplicateHammingDistance)
                {
                    return new ImageCheckResult(false, "Duplicate or near-duplicate image.", bitmap.Width, bitmap.Height, hash);
                }
            }
        }

        return new ImageCheckResult(true, null, bitmap.Width, bitmap.Height, hash);
    }

    public static ImageCheckResult ValidateDimensions(int width, int height, FilterConfig filter)
    {
        if (width < filter.MinWidth || height < filter.MinHeight)
        {
            return new ImageCheckResult(false, $"Below minimum resolution ({width}x{height} < {filter.MinWidth}x{filter.MinHeight}).", width, height);
        }

        if (width > filter.MaxWidth || height > filter.MaxHeight)
        {
            return new ImageCheckResult(false, $"Above maximum resolution ({width}x{height}).", width, height);
        }

        if (filter.Orientation == ImageOrientation.Landscape && width < height)
        {
            return new ImageCheckResult(false, "Orientation is portrait; expected landscape.", width, height);
        }

        if (filter.Orientation == ImageOrientation.Portrait && height < width)
        {
            return new ImageCheckResult(false, "Orientation is landscape; expected portrait.", width, height);
        }

        var aspect = (double)width / height;
        if (aspect < filter.AspectRatioMin || aspect > filter.AspectRatioMax)
        {
            return new ImageCheckResult(false, $"Aspect ratio {aspect:F2} outside [{filter.AspectRatioMin:F2}, {filter.AspectRatioMax:F2}].", width, height);
        }

        return new ImageCheckResult(true, null, width, height);
    }

    public static ulong ComputeDifferenceHash(SKBitmap source)
    {
        var info = new SKImageInfo(9, 8, SKColorType.Gray8, SKAlphaType.Opaque);
        using var resized = source.Resize(info, SKFilterQuality.Low);
        if (resized is null)
        {
            return 0;
        }

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var left = resized.GetPixel(x, y).Red;
                var right = resized.GetPixel(x + 1, y).Red;
                if (left > right)
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
