using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Storage;

/// <summary>
/// One-time import of ActivityWatch's peewee SQLite into the own EventStore
/// (plan M3 + graft #4). Safety rails:
///  - source opened READ-ONLY, and always from a pre-made COPY (never the live file);
///  - when importing from the DEFAULT live path, aw-server must be DOWN (probe first) —
///    reading a live rollback-journal DB risks torn reads;
///  - datastr re-canonicalized through JsonCanonical so the first post-cutover heartbeat
///    merges seamlessly with the last imported event;
///  - per-bucket row-count verification, abort on mismatch.
/// </summary>
public static class AwImporter
{
    public static string DefaultSourcePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "activitywatch", "activitywatch", "aw-server", "peewee-sqlite.v2.db");

    /// <summary>
    /// Auto mode (no explicitSource): runs only when the store is EMPTY, the default peewee
    /// DB exists, and aw-server does not answer the probe. Explicit mode (--import-aw path):
    /// path is treated as a user-made copy — no probe; requires an empty store unless force.
    /// </summary>
    public static async Task RunIfNeededAsync(
        EventStore store, string awProbeUrl, string? explicitSource, bool force = false, CancellationToken ct = default)
    {
        var isEmpty = store.GetBuckets().Count == 0;
        var source = explicitSource ?? DefaultSourcePath;

        if (explicitSource is null)
        {
            if (!isEmpty || !File.Exists(source)) return; // nothing to do — normal startup
            if (await AwAliveAsync(awProbeUrl, ct))
            {
                Log.Error("aw import SKIPPED: aw-server still answers — stop it first (live peewee DB = torn-read risk)");
                return;
            }
        }
        else
        {
            if (!File.Exists(source)) throw new FileNotFoundException("aw import: source not found", source);
            if (!isEmpty && !force)
                throw new InvalidOperationException("aw import: store not empty — pass --force to wipe and reimport");
            if (!isEmpty)
            {
                foreach (var b in store.GetBuckets())
                {
                    // force wipe: importer owns the store at this point (startup, pre-Kestrel)
                    await store.DeleteBucketAsync(b.Id, ct);
                }
            }
        }

        // work from a copy even in auto mode — one cheap file copy buys crash safety
        var workCopy = source + ".import-copy";
        File.Copy(source, workCopy, overwrite: true);
        var journal = source + "-journal";
        try
        {
            if (File.Exists(journal)) File.Copy(journal, workCopy + "-journal", overwrite: true);

            var connString = new SqliteConnectionStringBuilder { DataSource = workCopy, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var src = new SqliteConnection(connString);
            src.Open();

            var buckets = new List<(long Key, string Id, string Type)>();
            using (var cmd = src.CreateCommand())
            {
                cmd.CommandText = "SELECT key, id, type FROM bucketmodel";
                using var r = cmd.ExecuteReader();
                while (r.Read()) buckets.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
            }

            long totalImported = 0;
            foreach (var (key, id, type) in buckets)
            {
                if (id.StartsWith("tracker-smoke", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"aw import: skipping junk bucket {id}");
                    continue;
                }
                await store.EnsureBucketAsync(id, type, ct);

                var rows = new List<(long TsUs, long DurUs, string Datastr)>();
                long sourceCount = 0;
                using (var cmd = src.CreateCommand())
                {
                    cmd.CommandText = "SELECT timestamp, duration, datastr FROM eventmodel WHERE bucket_id=@k ORDER BY timestamp";
                    cmd.Parameters.AddWithValue("@k", key);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        sourceCount++;
                        rows.Add((ParseTsUs(r.GetValue(0)), ParseDurUs(r.GetValue(1)), Canonical(r.GetString(2))));
                        if (rows.Count >= 5000)
                        {
                            await store.ImportBatchAsync(id, rows, ct);
                            totalImported += rows.Count;
                            rows.Clear();
                        }
                    }
                }
                if (rows.Count > 0)
                {
                    await store.ImportBatchAsync(id, rows, ct);
                    totalImported += rows.Count;
                }

                // verification (plan M3): source row count must equal what landed
                var (imported, sumSec) = store.Stats(id, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1));
                if (imported != sourceCount)
                    throw new InvalidOperationException(
                        $"aw import VERIFICATION FAILED for {id}: source {sourceCount} vs imported {imported} — aborting");
                Log.Info($"aw import: {id} — {imported} events, {sumSec / 3600:F1}h total");
            }
            Log.Info($"aw import DONE: {totalImported} events from {source}");
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                File.Delete(workCopy);
                if (File.Exists(workCopy + "-journal")) File.Delete(workCopy + "-journal");
            }
            catch { /* best effort */ }
        }
    }

    private static async Task<bool> AwAliveAsync(string awUrl, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync(awUrl.TrimEnd('/') + "/api/0/info", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static long ParseTsUs(object value)
    {
        // peewee stores DATETIME as TEXT "2026-07-10 14:28:42.168000+00:00"; older rows may
        // lack the offset — treat offset-less values as UTC, never local
        var s = Convert.ToString(value, CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException("aw import: null timestamp");
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            throw new InvalidOperationException($"aw import: unparsable timestamp '{s}'");
        return EventStore.ToUs(dto);
    }

    private static long ParseDurUs(object value) => value switch
    {
        // DECIMAL(10,5) comes back as double, long, or TEXT depending on how peewee wrote it
        double d => (long)Math.Round(d * 1_000_000),
        long l => l * 1_000_000,
        string s => (long)Math.Round(double.Parse(s, CultureInfo.InvariantCulture) * 1_000_000),
        decimal m => (long)Math.Round((double)m * 1_000_000),
        _ => throw new InvalidOperationException($"aw import: unexpected duration type {value.GetType()}"),
    };

    private static string Canonical(string datastr)
    {
        using var doc = JsonDocument.Parse(datastr);
        return JsonCanonical.Canonicalize(doc.RootElement);
    }
}
