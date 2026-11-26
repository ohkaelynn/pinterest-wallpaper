using System.Text.Json;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Media;

namespace PinterestWallpaper.Core.Pinterest;

public sealed record ExtractedPin(
    string PinId,
    string PinUrl,
    string ImageUrl,
    int Width,
    int Height);

public static class PinterestPinExtractor
{
    public static IReadOnlyList<ExtractedPin> ExtractFromJson(string json, FilterConfig? preFilter = null)
    {
        var results = new List<ExtractedPin>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using var doc = JsonDocument.Parse(json);
            WalkElement(doc.RootElement, results, seen, preFilter);
        }
        catch (JsonException)
        {
            // Ignore non-JSON or partial responses.
        }

        return results;
    }

    private static void WalkElement(
        JsonElement element,
        List<ExtractedPin> results,
        HashSet<string> seen,
        FilterConfig? preFilter)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryExtractPinObject(element, out var pin) && seen.Add(pin.PinId))
            {
                if (preFilter is null ||
                    (pin.Width == 0 && pin.Height == 0) ||
                    ImageFilter.ValidateDimensions(pin.Width, pin.Height, preFilter).IsValid)
                {
                    results.Add(pin);
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    WalkElement(prop.Value, results, seen, preFilter);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    WalkElement(item, results, seen, preFilter);
                }
            }
        }
    }

    private static bool TryExtractPinObject(JsonElement obj, out ExtractedPin pin)
    {
        pin = default!;

        if (!obj.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var pinId = idProp.GetString();
        if (string.IsNullOrWhiteSpace(pinId) || !pinId.All(char.IsDigit))
        {
            return false;
        }

        // Skip video pins if marked as video.
        if (obj.TryGetProperty("videos", out var videosProp) && videosProp.ValueKind == JsonValueKind.Object)
        {
            return false;
        }

        if (!obj.TryGetProperty("images", out var imagesProp) || imagesProp.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetBestImage(imagesProp, out var imageUrl, out var width, out var height))
        {
            return false;
        }

        pin = new ExtractedPin(
            PinId: pinId,
            PinUrl: $"https://www.pinterest.com/pin/{pinId}/",
            ImageUrl: imageUrl,
            Width: width,
            Height: height);
        return true;
    }

    private static bool TryGetBestImage(JsonElement imagesObj, out string url, out int width, out int height)
    {
        url = string.Empty;
        width = 0;
        height = 0;

        // Prefer Pinterest's full-resolution "orig" entry.
        if (imagesObj.TryGetProperty("orig", out var orig) && TryReadImageEntry(orig, out url, out width, out height))
        {
            return true;
        }

        // Otherwise pick the largest available resolution entry.
        long bestArea = -1;
        foreach (var prop in imagesObj.EnumerateObject())
        {
            if (TryReadImageEntry(prop.Value, out var candUrl, out var candW, out var candH))
            {
                var area = (long)candW * candH;
                if (area > bestArea)
                {
                    bestArea = area;
                    url = candUrl;
                    width = candW;
                    height = candH;
                }
            }
        }

        return !string.IsNullOrEmpty(url);
    }

    private static bool TryReadImageEntry(JsonElement entry, out string url, out int width, out int height)
    {
        url = string.Empty;
        width = 0;
        height = 0;

        if (entry.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!entry.TryGetProperty("url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = urlProp.GetString();
        if (!PinterestUrlGuard.IsAllowedPinterestImageUrl(candidate))
        {
            return false;
        }

        if (candidate!.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        url = candidate!;
        if (entry.TryGetProperty("width", out var wProp) && wProp.TryGetInt32(out var w))
        {
            width = w;
        }

        if (entry.TryGetProperty("height", out var hProp) && hProp.TryGetInt32(out var h))
        {
            height = h;
        }

        return true;
    }
}
