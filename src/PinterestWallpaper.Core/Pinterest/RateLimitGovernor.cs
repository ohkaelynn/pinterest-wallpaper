using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Storage;

namespace PinterestWallpaper.Core.Pinterest;

public sealed record SyncDecision(bool Allowed, string Reason);

public sealed class RateLimitGovernor
{
    private const string BackoffUntilKey = "backoff_until_utc";
    private const string ConsecutiveFailuresKey = "consecutive_failures";

    private static readonly TimeSpan[] BackoffSchedule =
    [
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(12)
    ];

    private readonly StateDatabase _db;
    private readonly Random _random;

    public RateLimitGovernor(StateDatabase db, Random? random = null)
    {
        _db = db;
        _random = random ?? Random.Shared;
    }

    public SyncDecision CanSync(FetchingConfig config, DateTimeOffset nowUtc, bool isManual = false)
    {
        var backoffUntil = GetBackoffUntilUtc();
        if (backoffUntil is not null && nowUtc < backoffUntil.Value)
        {
            var remaining = backoffUntil.Value - nowUtc;
            return new SyncDecision(false, $"In rate-limit backoff for another {Math.Ceiling(remaining.TotalMinutes)} min.");
        }

        // Manual "Sync Now" bypasses auto-sync cooldown & daily cap (still obeys 429 backoff).
        if (isManual)
        {
            return new SyncDecision(true, "Allowed.");
        }

        var lastSync = _db.GetLastSyncTimeUtc();
        if (lastSync is not null)
        {
            var requiredCooldown = TimeSpan.FromMinutes(Math.Max(config.MinSyncCooldownMinutes, 5));
            var elapsed = nowUtc - lastSync.Value;
            if (elapsed < requiredCooldown)
            {
                var remaining = requiredCooldown - elapsed;
                return new SyncDecision(false, $"Cooldown active ({Math.Ceiling(remaining.TotalMinutes)} min remaining).");
            }
        }

        var maxPerDay = Math.Clamp(config.MaxSyncsPerDay, 1, 48);
        var syncsLast24h = _db.GetSyncCountSince(nowUtc.AddHours(-24));
        if (syncsLast24h >= maxPerDay)
        {
            return new SyncDecision(false, $"Daily sync budget reached ({syncsLast24h}/{maxPerDay}).");
        }

        return new SyncDecision(true, "Allowed.");
    }

    public SyncDecision ShouldAutoSync(FetchingConfig config, DateTimeOffset nowUtc)
    {
        var unseen = _db.GetUnseenCount();
        if (unseen >= config.LowWaterMark)
        {
            return new SyncDecision(false, $"Local pool healthy ({unseen} unseen >= {config.LowWaterMark}).");
        }

        return CanSync(config, nowUtc, isManual: false);
    }

    public TimeSpan GetHumanStepDelay() =>
        TimeSpan.FromMilliseconds(_random.Next(700, 1300));

    public TimeSpan GetImageDownloadDelay() =>
        TimeSpan.FromMilliseconds(_random.Next(100, 200));

    public void RecordSuccess(int pinsAdded, DateTimeOffset nowUtc)
    {
        _db.RecordSync(nowUtc, "ok", pinsAdded);
        _db.DeleteStateValue(BackoffUntilKey);
        _db.DeleteStateValue(ConsecutiveFailuresKey);
    }

    public TimeSpan RecordRateLimitOrChallenge(DateTimeOffset nowUtc, string status = "rate_limited")
    {
        var failures = GetConsecutiveFailures() + 1;
        var index = Math.Min(failures - 1, BackoffSchedule.Length - 1);
        var delay = BackoffSchedule[index];

        _db.RecordSync(nowUtc, status, 0);
        _db.SetStateValue(ConsecutiveFailuresKey, failures.ToString());
        _db.SetStateValue(BackoffUntilKey, (nowUtc + delay).ToString("O"));
        return delay;
    }

    public DateTimeOffset? GetBackoffUntilUtc()
    {
        var raw = _db.GetStateValue(BackoffUntilKey);
        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : null;
    }

    public int GetConsecutiveFailures()
    {
        var raw = _db.GetStateValue(ConsecutiveFailuresKey);
        return int.TryParse(raw, out var parsed) ? parsed : 0;
    }
}
