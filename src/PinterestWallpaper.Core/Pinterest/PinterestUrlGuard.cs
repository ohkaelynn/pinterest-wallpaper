using System.Text.RegularExpressions;

namespace PinterestWallpaper.Core.Pinterest;

public static partial class PinterestUrlGuard
{
    public const long MaxImageDownloadBytes = 50L * 1024L * 1024L; // 50 MB

    [GeneratedRegex(@"^(?:[a-z0-9-]+\.)?pinterest\.[a-z]{2,3}(?:\.[a-z]{2})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PinterestHostRegex();

    [GeneratedRegex(@"^(?:[a-z0-9-]+\.)?pinimg\.com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PinimgHostRegex();

    public static bool IsAllowedPinterestPageUrl(string? url)
    {
        if (!TryParseHttpsUri(url, out var uri))
        {
            return false;
        }

        return PinterestHostRegex().IsMatch(uri.Host);
    }

    public static bool IsAllowedPinterestImageUrl(string? url)
    {
        if (!TryParseHttpsUri(url, out var uri))
        {
            return false;
        }

        return PinimgHostRegex().IsMatch(uri.Host);
    }

    private static bool TryParseHttpsUri(string? url, out Uri uri)
    {
        uri = default!;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
