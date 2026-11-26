using Microsoft.Data.Sqlite;

namespace PinterestWallpaper.Core.Storage;

public sealed class StateDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public StateDatabase(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS pins (
                pin_id TEXT PRIMARY KEY,
                source_type TEXT NOT NULL,
                source_value TEXT NOT NULL,
                pin_url TEXT NOT NULL,
                local_path TEXT NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                perceptual_hash INTEGER NOT NULL,
                added_at_utc TEXT NOT NULL,
                last_shown_at_utc TEXT NULL,
                is_favorite INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS blocklist (
                pin_id TEXT PRIMARY KEY,
                blocked_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pin_id TEXT NOT NULL,
                shown_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                synced_at_utc TEXT NOT NULL,
                status TEXT NOT NULL,
                pins_added INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS kv_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertPin(PinRecord pin)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pins (pin_id, source_type, source_value, pin_url, local_path, width, height, perceptual_hash, added_at_utc, last_shown_at_utc, is_favorite)
            VALUES ($pinId, $sourceType, $sourceValue, $pinUrl, $localPath, $width, $height, $hash, $addedAt, $lastShownAt, $isFav)
            ON CONFLICT(pin_id) DO UPDATE SET
                local_path = excluded.local_path,
                width = excluded.width,
                height = excluded.height,
                perceptual_hash = excluded.perceptual_hash;
            """;
        cmd.Parameters.AddWithValue("$pinId", pin.PinId);
        cmd.Parameters.AddWithValue("$sourceType", pin.SourceType);
        cmd.Parameters.AddWithValue("$sourceValue", pin.SourceValue);
        cmd.Parameters.AddWithValue("$pinUrl", pin.PinUrl);
        cmd.Parameters.AddWithValue("$localPath", pin.LocalPath);
        cmd.Parameters.AddWithValue("$width", pin.Width);
        cmd.Parameters.AddWithValue("$height", pin.Height);
        cmd.Parameters.AddWithValue("$hash", unchecked((long)pin.PerceptualHash));
        cmd.Parameters.AddWithValue("$addedAt", pin.AddedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$lastShownAt", (object?)pin.LastShownAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$isFav", pin.IsFavorite ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public bool IsBlocked(string pinId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM blocklist WHERE pin_id = $pinId LIMIT 1;";
        cmd.Parameters.AddWithValue("$pinId", pinId);
        return cmd.ExecuteScalar() is not null;
    }

    public void BlockPin(string pinId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO blocklist (pin_id, blocked_at_utc) VALUES ($pinId, $now);
            DELETE FROM pins WHERE pin_id = $pinId;
            """;
        cmd.Parameters.AddWithValue("$pinId", pinId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<PinRecord> GetAvailablePins()
    {
        var results = new List<PinRecord>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.pin_id, p.source_type, p.source_value, p.pin_url, p.local_path,
                   p.width, p.height, p.perceptual_hash, p.added_at_utc, p.last_shown_at_utc, p.is_favorite
            FROM pins p
            LEFT JOIN blocklist b ON p.pin_id = b.pin_id
            WHERE b.pin_id IS NULL
            ORDER BY COALESCE(p.last_shown_at_utc, '') ASC, p.added_at_utc ASC;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadPin(reader));
        }

        return results;
    }

    public IReadOnlyList<ulong> GetAllPerceptualHashes()
    {
        var hashes = new List<ulong>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT perceptual_hash FROM pins;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hashes.Add(unchecked((ulong)reader.GetInt64(0)));
        }

        return hashes;
    }

    public void RecordShown(string pinId, DateTimeOffset shownAtUtc)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE pins SET last_shown_at_utc = $now WHERE pin_id = $pinId;
            INSERT INTO history (pin_id, shown_at_utc) VALUES ($pinId, $now);
            """;
        cmd.Parameters.AddWithValue("$pinId", pinId);
        cmd.Parameters.AddWithValue("$now", shownAtUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public PinRecord? GetPinById(string pinId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT pin_id, source_type, source_value, pin_url, local_path,
                   width, height, perceptual_hash, added_at_utc, last_shown_at_utc, is_favorite
            FROM pins WHERE pin_id = $pinId LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$pinId", pinId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadPin(reader) : null;
    }

    public void RemovePin(string pinId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM pins WHERE pin_id = $pinId;";
        cmd.Parameters.AddWithValue("$pinId", pinId);
        cmd.ExecuteNonQuery();
    }

    public int ClearAllPins()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM pins; DELETE FROM history;";
        return cmd.ExecuteNonQuery();
    }

    public void SetFavorite(string pinId, bool isFavorite)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE pins SET is_favorite = $fav WHERE pin_id = $pinId;";
        cmd.Parameters.AddWithValue("$pinId", pinId);
        cmd.Parameters.AddWithValue("$fav", isFavorite ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public int GetUnseenCount()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM pins p
            LEFT JOIN blocklist b ON p.pin_id = b.pin_id
            WHERE b.pin_id IS NULL AND p.last_shown_at_utc IS NULL;
            """;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void RecordSync(DateTimeOffset syncedAtUtc, string status, int pinsAdded)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_log (synced_at_utc, status, pins_added)
            VALUES ($now, $status, $added);
            """;
        cmd.Parameters.AddWithValue("$now", syncedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$added", pinsAdded);
        cmd.ExecuteNonQuery();
    }

    public DateTimeOffset? GetLastSyncTimeUtc()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT synced_at_utc FROM sync_log ORDER BY id DESC LIMIT 1;";
        var result = cmd.ExecuteScalar() as string;
        return result is null ? null : DateTimeOffset.Parse(result);
    }

    public int GetSyncCountSince(DateTimeOffset sinceUtc)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sync_log WHERE synced_at_utc >= $since;";
        cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public string? GetStateValue(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv_state WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetStateValue(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO kv_state (key, value) VALUES ($key, $val)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$val", value);
        cmd.ExecuteNonQuery();
    }

    public void DeleteStateValue(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM kv_state WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<PinRecord> GetEvictablePins()
    {
        var results = new List<PinRecord>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT pin_id, source_type, source_value, pin_url, local_path,
                   width, height, perceptual_hash, added_at_utc, last_shown_at_utc, is_favorite
            FROM pins
            WHERE is_favorite = 0 AND last_shown_at_utc IS NOT NULL
            ORDER BY last_shown_at_utc ASC;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadPin(reader));
        }

        return results;
    }

    private static PinRecord ReadPin(SqliteDataReader reader)
    {
        return new PinRecord(
            PinId: reader.GetString(0),
            SourceType: reader.GetString(1),
            SourceValue: reader.GetString(2),
            PinUrl: reader.GetString(3),
            LocalPath: reader.GetString(4),
            Width: reader.GetInt32(5),
            Height: reader.GetInt32(6),
            PerceptualHash: unchecked((ulong)reader.GetInt64(7)),
            AddedAtUtc: DateTimeOffset.Parse(reader.GetString(8)),
            LastShownAtUtc: reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            IsFavorite: reader.GetInt32(10) != 0);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
