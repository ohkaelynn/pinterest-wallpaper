using System.Security.Cryptography;
using System.Text;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Media;
using PinterestWallpaper.Core.Storage;

namespace PinterestWallpaper.Core.Rotation;

public sealed class RotationService
{
    private readonly StateDatabase _db;
    private readonly IWallpaperApplier _applier;
    private readonly ISystemStateProvider _systemState;
    private readonly Random _random;
    private readonly List<IReadOnlyList<PinRecord>> _sessionHistory = [];
    private int _historyIndex = -1;

    public bool IsPaused { get; set; }

    public IReadOnlyList<PinRecord> CurrentPins =>
        _historyIndex >= 0 && _historyIndex < _sessionHistory.Count
            ? _sessionHistory[_historyIndex]
            : [];

    public RotationService(
        StateDatabase db,
        IWallpaperApplier applier,
        ISystemStateProvider systemState,
        Random? random = null)
    {
        _db = db;
        _applier = applier;
        _systemState = systemState;
        _random = random ?? Random.Shared;
    }

    public TimeSpan GetNextInterval(RotationConfig config)
    {
        var baseMinutes = Math.Clamp(config.IntervalMinutes, 1, 1440);
        var jitterFraction = Math.Clamp(config.JitterPercent, 0, 50) / 100.0;
        var delta = (_random.NextDouble() * 2.0 - 1.0) * jitterFraction;
        var minutes = Math.Max(1.0, baseMinutes * (1.0 + delta));
        return TimeSpan.FromMinutes(minutes);
    }

    public bool ShouldSkipScheduledRotation(RotationConfig config)
    {
        if (!config.Enabled || IsPaused)
        {
            return true;
        }

        if (config.PauseOnBattery && _systemState.IsOnBattery())
        {
            return true;
        }

        if (config.PauseOnFullscreen && _systemState.IsFullscreenAppActive())
        {
            return true;
        }

        return false;
    }

    public bool NextWallpaper(AppConfig config, bool isManual = true)
    {
        if (!isManual && ShouldSkipScheduledRotation(config.Rotation))
        {
            return false;
        }

        // If user navigated backward earlier, step forward through existing history first.
        if (_historyIndex >= 0 && _historyIndex < _sessionHistory.Count - 1)
        {
            _historyIndex++;
            var existing = _sessionHistory[_historyIndex];
            if (existing.All(p => File.Exists(p.LocalPath)))
            {
                _applier.ApplyWallpapers(existing.Select(p => p.LocalPath).ToList(), config.Display);
                return true;
            }
        }

        var available = _db.GetAvailablePins()
            .Where(p => File.Exists(p.LocalPath))
            .ToList();

        if (available.Count == 0)
        {
            return false;
        }

        var currentIds = CurrentPins.Select(p => p.PinId).ToHashSet();
        var needed = config.Display.PerMonitorWallpapers ? Math.Max(1, _applier.GetMonitorCount()) : 1;

        var candidates = available.Where(p => !currentIds.Contains(p.PinId)).ToList();
        if (candidates.Count < needed)
        {
            candidates = available;
        }

        // Prioritize unseen pins first, then oldest-shown pins, with slight randomness.
        var unseen = candidates.Where(p => p.LastShownAtUtc is null).ToList();
        var pool = unseen.Count >= needed ? unseen : candidates;

        var chosen = new List<PinRecord>(needed);
        var remaining = new List<PinRecord>(pool);
        for (var i = 0; i < needed && remaining.Count > 0; i++)
        {
            var idx = _random.Next(remaining.Count);
            chosen.Add(remaining[idx]);
            remaining.RemoveAt(idx);
        }

        if (chosen.Count == 0)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var pin in chosen)
        {
            _db.RecordShown(pin.PinId, now);
        }

        _applier.ApplyWallpapers(chosen.Select(p => p.LocalPath).ToList(), config.Display);

        if (_historyIndex < _sessionHistory.Count - 1)
        {
            _sessionHistory.RemoveRange(_historyIndex + 1, _sessionHistory.Count - _historyIndex - 1);
        }

        _sessionHistory.Add(chosen);
        _historyIndex = _sessionHistory.Count - 1;
        return true;
    }

    public bool PreviousWallpaper(AppConfig config)
    {
        if (_historyIndex <= 0)
        {
            return false;
        }

        _historyIndex--;
        var previous = _sessionHistory[_historyIndex];
        if (!previous.All(p => File.Exists(p.LocalPath)))
        {
            return false;
        }

        _applier.ApplyWallpapers(previous.Select(p => p.LocalPath).ToList(), config.Display);
        return true;
    }

    public bool SkipAndBlockCurrent(AppConfig config)
    {
        foreach (var pin in CurrentPins)
        {
            _db.BlockPin(pin.PinId);
        }

        return NextWallpaper(config, isManual: true);
    }

    public int ImportLocalFolder(string folderPath, FilterConfig filter, string cacheDirectory)
    {
        if (!Directory.Exists(folderPath))
        {
            return 0;
        }

        Directory.CreateDirectory(cacheDirectory);
        var hashes = _db.GetAllPerceptualHashes().ToList();
        var added = 0;

        foreach (var file in Directory.EnumerateFiles(folderPath))
        {
            if (!ImageFilter.HasSupportedExtension(file))
            {
                continue;
            }

            var pinId = "local-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file)))[..16].ToLowerInvariant();
            if (_db.IsBlocked(pinId) || _db.GetPinById(pinId) is not null)
            {
                continue;
            }

            var check = ImageFilter.ValidateFile(file, filter, hashes);
            if (!check.IsValid)
            {
                continue;
            }

            var destPath = Path.Combine(cacheDirectory, pinId + Path.GetExtension(file).ToLowerInvariant());
            File.Copy(file, destPath, overwrite: true);

            _db.UpsertPin(new PinRecord(
                PinId: pinId,
                SourceType: "local",
                SourceValue: folderPath,
                PinUrl: string.Empty,
                LocalPath: destPath,
                Width: check.Width,
                Height: check.Height,
                PerceptualHash: check.PerceptualHash,
                AddedAtUtc: DateTimeOffset.UtcNow,
                LastShownAtUtc: null,
                IsFavorite: false));

            hashes.Add(check.PerceptualHash);
            added++;
        }

        return added;
    }
}
