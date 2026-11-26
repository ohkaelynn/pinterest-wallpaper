using System.Text.Json;

namespace PinterestWallpaper.Core.Pinterest;

public sealed record UserBoard(string Name, string Url)
{
    public override string ToString() => Name;
}

public static class PinterestBoardExtractor
{
    public static IReadOnlyList<UserBoard> ExtractFromJson(string json)
    {
        var results = new List<UserBoard>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(json);
            Walk(doc.RootElement, results, seenUrls);
        }
        catch (JsonException)
        {
            // Ignore non-JSON payloads.
        }

        return results.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(JsonElement el, List<UserBoard> results, HashSet<string> seenUrls)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (TryReadBoard(el, out var board) && seenUrls.Add(board.Url))
            {
                results.Add(board);
            }

            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    Walk(prop.Value, results, seenUrls);
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    Walk(item, results, seenUrls);
                }
            }
        }
    }

    private static bool TryReadBoard(JsonElement obj, out UserBoard board)
    {
        board = default!;

        var isBoardType =
            (obj.TryGetProperty("type", out var typeProp) &&
             typeProp.ValueKind == JsonValueKind.String &&
             string.Equals(typeProp.GetString(), "board", StringComparison.OrdinalIgnoreCase)) ||
            obj.TryGetProperty("pin_count", out _);

        if (!isBoardType)
        {
            return false;
        }

        if (!obj.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!obj.TryGetProperty("url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var name = nameProp.GetString()?.Trim();
        var rawUrl = urlProp.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        // Normalize relative board paths like "/username/wallpapers/" to full HTTPS URLs.
        var fullUrl = rawUrl.StartsWith("/", StringComparison.Ordinal)
            ? $"https://www.pinterest.com{rawUrl}"
            : rawUrl;

        if (!PinterestUrlGuard.IsAllowedPinterestPageUrl(fullUrl))
        {
            return false;
        }

        // Board URLs have at least /username/board-slug/ (2 path segments).
        var segments = new Uri(fullUrl).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0].Equals("pin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        board = new UserBoard(name, fullUrl);
        return true;
    }
}
