using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tracker.Shared.Aw;
using Tracker.Shared.Logging;
using Tracker.Shared.Storage;

namespace Tracker.Daemon.Storage;

/// <summary>
/// Own SQLite event store replacing aw-server (plan docs/PLAN-2026-07-10-remove-activitywatch.md).
/// Single writer by construction: ALL merge+write sections run under one SemaphoreSlim —
/// Kestrel serves watcher/browser/claude heartbeats on concurrent threads and an unlocked
/// read-modify-write on the last event would silently split/duplicate events (plan graft #1).
/// Timestamps are stored as int64 epoch MICROseconds (µs) so peewee-imported precision
/// survives byte-exact (graft #3). Startup: quick_check with rename-recreate on corruption
/// (graft #5) and PRAGMA user_version fail-fast (graft #7).
/// </summary>
public sealed class EventStore : IEventStore, IDisposable
{
    private const long SchemaVersion = 1;

    private readonly string _dbPath;
    private readonly string _clientName;
    private readonly string _hostname;
    private readonly string _connString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // bucket id → key, and per-bucket last event (max start ts) for the merge
    private readonly Dictionary<string, long> _bucketKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<long, LastEvent> _last = new();
    private long _clampedNegativeDurations;
    private long _rejectedBackwardsMerges;

    private sealed record LastEvent(long Id, long TsUs, long EndUs, string Datastr);

    public sealed record BucketInfo(string Id, string Type, string Client, string Hostname, string Created);

    public EventStore(string dbPath, string clientName, string hostname)
    {
        _dbPath = dbPath;
        _clientName = clientName;
        _hostname = hostname;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = true }.ToString();
        try
        {
            Init();
        }
        catch (SqliteException ex)
        {
            // unreadable file = same treatment as failed quick_check: preserve + recreate,
            // otherwise the daemon boot-loops and tracking silently stops
            Log.Error($"events.db unusable ({ex.Message}) — renaming aside and recreating");
            RenameCorruptAside();
            Init();
        }
    }

    public string DbPath => _dbPath;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Init()
    {
        using var c = Open();
        if (Scalar<string>(c, "PRAGMA quick_check(1)") != "ok")
        {
            c.Close();
            SqliteConnection.ClearAllPools();
            Log.Error("events.db failed quick_check — renaming aside and recreating");
            RenameCorruptAside();
            using var c2 = Open();
            InitSchema(c2);
            return;
        }
        InitSchema(c);
    }

    private void InitSchema(SqliteConnection c)
    {
        var version = Scalar<long>(c, "PRAGMA user_version");
        if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"events.db schema v{version} is newer than this binary supports (v{SchemaVersion}) — " +
                "refusing to touch it. Update the daemon or restore the matching version.");
        if (version == SchemaVersion) { Exec(c, "PRAGMA journal_mode=WAL;"); return; }

        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS buckets (
              key      INTEGER PRIMARY KEY,
              id       TEXT NOT NULL UNIQUE,
              type     TEXT NOT NULL,
              client   TEXT NOT NULL,
              hostname TEXT NOT NULL,
              created  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS events (
              id          INTEGER PRIMARY KEY,
              bucket_key  INTEGER NOT NULL REFERENCES buckets(key),
              ts_us       INTEGER NOT NULL,
              duration_us INTEGER NOT NULL,
              datastr     TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_events_bucket_ts ON events(bucket_key, ts_us);
            PRAGMA user_version=1;
            """);
        Log.Info($"EventStore initialized at {_dbPath} (schema v{SchemaVersion}, WAL)");
    }

    private void RenameCorruptAside()
    {
        SqliteConnection.ClearAllPools();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + suffix;
            if (File.Exists(p)) File.Move(p, $"{_dbPath}.corrupt-{stamp}{suffix}");
        }
        lock (_bucketKeys) { _bucketKeys.Clear(); }
        _last.Clear();
    }

    // ---- IEventStore ----

    public async Task<bool> EnsureBucketAsync(string bucketId, string type, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            using var c = Open();
            if (BucketKey(c, bucketId) is not null) return false;
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO buckets(id, type, client, hostname, created) VALUES(@i, @t, @c, @h, @cr)";
            cmd.Parameters.AddWithValue("@i", bucketId);
            cmd.Parameters.AddWithValue("@t", type);
            cmd.Parameters.AddWithValue("@c", _clientName);
            cmd.Parameters.AddWithValue("@h", _hostname);
            cmd.Parameters.AddWithValue("@cr", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            lock (_bucketKeys) { _bucketKeys.Remove(bucketId); }
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Throws KeyNotFoundException when the bucket does not exist (shim maps it to HTTP 404).</summary>
    public async Task HeartbeatAsync(
        string bucketId,
        IReadOnlyDictionary<string, object?> data,
        double pulsetimeSeconds,
        DateTimeOffset? timestamp = null,
        double durationSeconds = 0,
        CancellationToken ct = default)
    {
        var datastr = JsonCanonical.Serialize(data);
        var tsUs = ToUs(timestamp ?? DateTimeOffset.UtcNow);
        // clock-excursion guard: a FUTURE stamp (NTP overshoot, manual clock change, VM
        // restore) would poison _last — the merge requires ts >= last.TsUs, so after the
        // clock corrects back every real heartbeat inserts separately until wall time
        // passes the mark, and GetLast reloads the poisoned mark even across restarts.
        var nowUs = ToUs(DateTimeOffset.UtcNow);
        if (tsUs > nowUs + 5_000_000) tsUs = nowUs;
        var durUs = (long)Math.Round(durationSeconds * 1_000_000);
        if (durUs < 0)
        {
            durUs = 0;
            if (Interlocked.Increment(ref _clampedNegativeDurations) <= 5)
                Log.Warn($"heartbeat with negative duration clamped to 0 (bucket {bucketId})");
        }
        var pulseUs = (long)Math.Round(pulsetimeSeconds * 1_000_000);

        await _writeGate.WaitAsync(ct);
        try
        {
            using var c = Open();
            var key = BucketKey(c, bucketId) ?? throw new KeyNotFoundException($"bucket not found: {bucketId}");
            var last = GetLast(c, key);

            // merge guard (plan D3): identical data, hb starts at/after the last event's
            // start (a clock step-back must NOT mutate history backwards), and within
            // pulsetime of the last event's end
            if (last is not null
                && last.Datastr == datastr
                && tsUs >= last.TsUs
                && tsUs <= last.EndUs + pulseUs)
            {
                var newEndUs = Math.Max(last.EndUs, tsUs + durUs); // max-end: AFK backfill + queued-replay idempotency
                using var upd = c.CreateCommand();
                upd.CommandText = "UPDATE events SET duration_us=@d WHERE id=@id";
                upd.Parameters.AddWithValue("@d", newEndUs - last.TsUs);
                upd.Parameters.AddWithValue("@id", last.Id);
                upd.ExecuteNonQuery();
                _last[key] = last with { EndUs = newEndUs };
                return;
            }
            if (last is not null && last.Datastr == datastr && tsUs < last.TsUs
                && Interlocked.Increment(ref _rejectedBackwardsMerges) <= 5)
            {
                Log.Warn($"heartbeat older than the bucket's last event — inserted separately, not merged (bucket {bucketId})");
            }

            using var ins = c.CreateCommand();
            ins.CommandText = "INSERT INTO events(bucket_key, ts_us, duration_us, datastr) VALUES(@b, @t, @d, @s); SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("@b", key);
            ins.Parameters.AddWithValue("@t", tsUs);
            ins.Parameters.AddWithValue("@d", durUs);
            ins.Parameters.AddWithValue("@s", datastr);
            var id = (long)ins.ExecuteScalar()!;
            // the merge target stays the max-start event (aw-core "last event" semantics)
            if (last is null || tsUs >= last.TsUs)
                _last[key] = new LastEvent(id, tsUs, tsUs + durUs, datastr);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<List<AwEvent>> GetEventsRangeAsync(
        string bucketId, DateTimeOffset start, DateTimeOffset end, int limit = -1, CancellationToken ct = default)
    {
        using var c = Open();
        var key = BucketKey(c, bucketId);
        if (key is null) return Task.FromResult(new List<AwEvent>()); // AwClient's 404 → empty semantics

        using var cmd = c.CreateCommand();
        // overlap semantics: an event STARTING before the range but reaching into it (an
        // AFK/video span across midnight) must be returned too, or the next day loses its
        // share. @lb keeps the scan index-bounded; only an event longer than 7 days could
        // be missed — far beyond any real afk/dormant span.
        cmd.CommandText = "SELECT ts_us, duration_us, datastr FROM events"
                          + " WHERE bucket_key=@b AND ts_us<=@e AND ts_us>=@lb AND ts_us+duration_us>=@s ORDER BY ts_us DESC"
                          + (limit >= 0 ? " LIMIT @n" : "");
        cmd.Parameters.AddWithValue("@b", key.Value);
        cmd.Parameters.AddWithValue("@s", ToUs(start));
        cmd.Parameters.AddWithValue("@e", ToUs(end));
        cmd.Parameters.AddWithValue("@lb", ToUs(start) - 7L * 24 * 3600 * 1_000_000);
        if (limit >= 0) cmd.Parameters.AddWithValue("@n", limit);
        var list = new List<AwEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            using var doc = JsonDocument.Parse(r.GetString(2));
            list.Add(new AwEvent(FromUs(r.GetInt64(0)), r.GetInt64(1) / 1_000_000.0, doc.RootElement.Clone()));
        }
        return Task.FromResult(list);
    }

    // ---- shim / importer / diff support ----

    public bool BucketExists(string bucketId)
    {
        using var c = Open();
        return BucketKey(c, bucketId) is not null;
    }

    public List<BucketInfo> GetBuckets()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, type, client, hostname, created FROM buckets ORDER BY id";
        var list = new List<BucketInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new BucketInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    /// <summary>Deletes a bucket and its events. Callers must gate WHICH buckets may be deleted.</summary>
    public async Task<bool> DeleteBucketAsync(string bucketId, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            using var c = Open();
            var key = BucketKey(c, bucketId);
            if (key is null) return false;
            Exec(c, $"DELETE FROM events WHERE bucket_key={key.Value}; DELETE FROM buckets WHERE key={key.Value};");
            lock (_bucketKeys) { _bucketKeys.Remove(bucketId); }
            _last.Remove(key.Value);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Bulk raw insert for the importer — no merging; caller pre-canonicalizes datastr.</summary>
    public async Task ImportBatchAsync(
        string bucketId, IReadOnlyList<(long TsUs, long DurUs, string Datastr)> rows, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            using var c = Open();
            var key = BucketKey(c, bucketId) ?? throw new KeyNotFoundException($"bucket not found: {bucketId}");
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO events(bucket_key, ts_us, duration_us, datastr) VALUES(@b, @t, @d, @s)";
            var pB = cmd.Parameters.Add("@b", SqliteType.Integer);
            var pT = cmd.Parameters.Add("@t", SqliteType.Integer);
            var pD = cmd.Parameters.Add("@d", SqliteType.Integer);
            var pS = cmd.Parameters.Add("@s", SqliteType.Text);
            foreach (var (tsUs, durUs, datastr) in rows)
            {
                pB.Value = key;
                pT.Value = tsUs;
                pD.Value = durUs;
                pS.Value = datastr;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            _last.Remove(key); // repopulate lazily so post-import heartbeats merge with imported history
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public (long Count, double SumDurationSeconds) Stats(string bucketId, DateTimeOffset start, DateTimeOffset end)
    {
        using var c = Open();
        var key = BucketKey(c, bucketId);
        if (key is null) return (0, 0);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(duration_us),0) FROM events WHERE bucket_key=@b AND ts_us>=@s AND ts_us<=@e";
        cmd.Parameters.AddWithValue("@b", key.Value);
        cmd.Parameters.AddWithValue("@s", ToUs(start));
        cmd.Parameters.AddWithValue("@e", ToUs(end));
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1) / 1_000_000.0);
    }

    /// <summary>Online backup via VACUUM INTO (WAL-safe, consistent snapshot).</summary>
    public async Task BackupAsync(string destPath, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            if (File.Exists(destPath)) File.Delete(destPath); // VACUUM INTO refuses existing files
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "VACUUM INTO @p";
            cmd.Parameters.AddWithValue("@p", destPath);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ---- internals ----

    private long? BucketKey(SqliteConnection c, string bucketId)
    {
        lock (_bucketKeys)
        {
            if (_bucketKeys.TryGetValue(bucketId, out var cached)) return cached;
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key FROM buckets WHERE id=@i";
        cmd.Parameters.AddWithValue("@i", bucketId);
        var v = cmd.ExecuteScalar();
        if (v is null) return null;
        var key = (long)v;
        lock (_bucketKeys) { _bucketKeys[bucketId] = key; }
        return key;
    }

    /// <summary>Merge target = the bucket's max-start event; cached, lazily loaded (caller holds the write gate).</summary>
    private LastEvent? GetLast(SqliteConnection c, long bucketKey)
    {
        if (_last.TryGetValue(bucketKey, out var cached)) return cached;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, ts_us, duration_us, datastr FROM events WHERE bucket_key=@b ORDER BY ts_us DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@b", bucketKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var last = new LastEvent(r.GetInt64(0), r.GetInt64(1), r.GetInt64(1) + r.GetInt64(2), r.GetString(3));
        _last[bucketKey] = last;
        return last;
    }

    public static long ToUs(DateTimeOffset dto) => (dto.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
    public static DateTimeOffset FromUs(long us) => DateTimeOffset.UnixEpoch.AddTicks(us * 10);

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T? Scalar<T>(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is T t ? t : default;
    }

    public void Dispose()
    {
        _writeGate.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
