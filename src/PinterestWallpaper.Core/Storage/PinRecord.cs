namespace PinterestWallpaper.Core.Storage;

public sealed record PinRecord(
    string PinId,
    string SourceType,
    string SourceValue,
    string PinUrl,
    string LocalPath,
    int Width,
    int Height,
    ulong PerceptualHash,
    DateTimeOffset AddedAtUtc,
    DateTimeOffset? LastShownAtUtc,
    bool IsFavorite);
