using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Tracker.Shared.Aw;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Storage;

/// <summary>
/// aw-server /api/0 compat shim on the daemon's own Kestrel (:5601) — the watcher keeps its
/// AwClient/ResilientAwClient stack unchanged and just points here. Load-bearing semantics
/// (plan §agreement 3): 304 on existing bucket create, 404 on missing bucket, pulsetime query
/// param, start/end/limit range reads, events newest-first.
///
/// PARITY TEE (plan M4, temporary): with [storage] tee_aw_url set, every write is applied to
/// the local EventStore AND forwarded to the real aw-server; reads are PROXIED to aw-server so
/// it stays the source of truth until cutover. A failed forward returns 500 so the caller's
/// retry queue re-sends — the local re-apply merges idempotently (max-end).
/// </summary>
public static class StorageEndpoints
{
    public static void Map(WebApplication app, EventStore store, string hostname, string teeAwUrl)
    {
        var tee = string.IsNullOrWhiteSpace(teeAwUrl)
            ? null
            : new HttpClient { BaseAddress = new Uri(teeAwUrl.TrimEnd('/') + "/api/0/"), Timeout = TimeSpan.FromSeconds(5) };
        if (tee is not null)
            Log.Info($"PARITY TEE ACTIVE — writes dual-applied (local + {teeAwUrl}); reads proxied to aw-server");

        app.MapGet("/api/0/info", () =>
            Results.Json(new { hostname, version = "tracker-daemon", testing = false }));

        app.MapGet("/api/0/buckets", () =>
            Results.Json(store.GetBuckets().ToDictionary(
                b => b.Id,
                b => new { id = b.Id, type = b.Type, client = b.Client, hostname = b.Hostname, created = b.Created })));

        app.MapPost("/api/0/buckets/{bucketId}", async (string bucketId, HttpRequest req) =>
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var type = "unknown";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                    type = t.GetString()!;
            }
            catch { /* empty/odd body — keep "unknown", aw-server is equally lax */ }

            var created = await store.EnsureBucketAsync(bucketId, type);
            if (tee is not null) await TeeForwardAsync(tee, $"buckets/{Uri.EscapeDataString(bucketId)}", body);
            return created ? Results.Ok() : Results.StatusCode(StatusCodes.Status304NotModified);
        });

        app.MapPost("/api/0/buckets/{bucketId}/heartbeat", async (string bucketId, double pulsetime, HttpRequest req) =>
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            DateTimeOffset ts;
            double dur;
            Dictionary<string, object?> data;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                ts = root.GetProperty("timestamp").GetDateTimeOffset();
                dur = root.TryGetProperty("duration", out var d) ? d.GetDouble() : 0;
                data = new Dictionary<string, object?>();
                if (root.TryGetProperty("data", out var dt) && dt.ValueKind == JsonValueKind.Object)
                    foreach (var p in dt.EnumerateObject())
                        data[p.Name] = p.Value.Clone();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "malformed heartbeat: " + ex.Message });
            }

            try
            {
                await store.HeartbeatAsync(bucketId, data, pulsetime, ts, dur);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }

            if (tee is not null)
            {
                // post-cutover (2026-07-12): the LOCAL store is the source of truth — the
                // shadow forward must NEVER block or fail this response. The old awaited
                // forward + 502-on-failure held the watcher's whole write path hostage to
                // aw-server's health (5s timeout per heartbeat, queue backlog, window gaps).
                var pulse = pulsetime.ToString(CultureInfo.InvariantCulture);
                _ = TeeForwardAsync(tee, $"buckets/{Uri.EscapeDataString(bucketId)}/heartbeat?pulsetime={pulse}", body);
            }
            return Results.Ok();
        });

        app.MapGet("/api/0/buckets/{bucketId}/events", async (string bucketId, string? start, string? end, int? limit) =>
        {
            // post-cutover: reads are ALWAYS local (aw is just a shadow now)
            if (!store.BucketExists(bucketId)) return Results.NotFound();
            var s = start is not null ? DateTimeOffset.Parse(start, CultureInfo.InvariantCulture) : DateTimeOffset.UnixEpoch;
            var e = end is not null ? DateTimeOffset.Parse(end, CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow.AddDays(1);
            var events = await store.GetEventsRangeAsync(bucketId, s, e, limit ?? -1);
            return Results.Json(events.Select(ev => new
            {
                timestamp = ev.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
                duration = ev.Duration,
                data = ev.Data,
            }));
        });

        // needed by the watcher --smoke cleanup only — anything else stays undeletable
        app.MapDelete("/api/0/buckets/{bucketId}", async (string bucketId) =>
        {
            if (!bucketId.StartsWith("tracker-smoke", StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var deleted = await store.DeleteBucketAsync(bucketId);
            if (tee is not null)
                _ = TeeForwardAsync(tee, $"buckets/{Uri.EscapeDataString(bucketId)}?force=1", body: null, HttpMethod.Delete);
            return deleted ? Results.Ok() : Results.NotFound();
        });

        // ---- parity gate (plan M4): per-bucket/day diff, aw-server vs EventStore ----
        app.MapGet("/api/parity/diff", async (string? date) =>
        {
            if (tee is null) return Results.BadRequest(new { error = "tee inactive — set [storage] tee_aw_url" });
            var day = date is not null
                ? DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                : DateTime.Now.Date;
            var startLocal = new DateTimeOffset(day, DateTimeOffset.Now.Offset);
            var endLocal = startLocal.AddDays(1);

            var buckets = store.GetBuckets();
            var rows = new List<object>();
            var allMatch = true;
            foreach (var b in buckets)
            {
                var local = await store.GetEventsRangeAsync(b.Id, startLocal, endLocal);
                List<AwEvent> aw;
                try
                {
                    aw = await FetchAwEventsAsync(tee, b.Id, startLocal, endLocal);
                }
                catch (Exception ex)
                {
                    rows.Add(new { bucket = b.Id, error = "aw fetch failed: " + ex.Message });
                    allMatch = false;
                    continue;
                }

                // identical-timestamp ties come back in arbitrary relative order from both
                // stores — sort deterministically so ties never read as false mismatches
                local = local.OrderByDescending(e => e.Timestamp).ThenBy(e => e.Duration)
                    .ThenBy(e => JsonCanonical.Canonicalize(e.Data), StringComparer.Ordinal).ToList();
                aw = aw.OrderByDescending(e => e.Timestamp).ThenBy(e => e.Duration)
                    .ThenBy(e => JsonCanonical.Canonicalize(e.Data), StringComparer.Ordinal).ToList();

                object? firstMismatch = null;
                for (var i = 0; i < Math.Max(local.Count, aw.Count); i++)
                {
                    if (i >= local.Count || i >= aw.Count)
                    {
                        firstMismatch = new { index = i, side = i >= local.Count ? "missing-local" : "missing-aw" };
                        break;
                    }
                    var l = local[i];
                    var a = aw[i];
                    if (Math.Abs((l.Timestamp - a.Timestamp).TotalMilliseconds) > 1
                        || Math.Abs(l.Duration - a.Duration) > 0.001
                        || JsonCanonical.Canonicalize(l.Data) != JsonCanonical.Canonicalize(a.Data))
                    {
                        firstMismatch = new
                        {
                            index = i,
                            local = new { ts = l.Timestamp, dur = l.Duration, data = l.Data },
                            aw = new { ts = a.Timestamp, dur = a.Duration, data = a.Data },
                        };
                        break;
                    }
                }
                var match = firstMismatch is null;
                allMatch &= match;
                rows.Add(new
                {
                    bucket = b.Id,
                    match,
                    local = new { count = local.Count, sumSeconds = Math.Round(local.Sum(e => e.Duration), 3) },
                    aw = new { count = aw.Count, sumSeconds = Math.Round(aw.Sum(e => e.Duration), 3) },
                    firstMismatch,
                });
            }
            return Results.Json(new { date = day.ToString("yyyy-MM-dd"), allMatch, buckets = rows });
        });
    }

    private static async Task<bool> TeeForwardAsync(HttpClient tee, string pathAndQuery, string? body, HttpMethod? method = null)
    {
        try
        {
            using var req = new HttpRequestMessage(method ?? HttpMethod.Post, pathAndQuery);
            if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await tee.SendAsync(req);
            // 304 (bucket exists) is success; real failures bubble up as false
            return resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotModified;
        }
        catch (Exception ex)
        {
            Log.Warn("parity tee forward failed: " + ex.Message);
            return false;
        }
    }

    private static async Task<List<AwEvent>> FetchAwEventsAsync(HttpClient tee, string bucketId, DateTimeOffset start, DateTimeOffset end)
    {
        var url = $"buckets/{Uri.EscapeDataString(bucketId)}/events" +
                  $"?start={Uri.EscapeDataString(start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))}" +
                  $"&end={Uri.EscapeDataString(end.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))}" +
                  "&limit=-1";
        var resp = await tee.GetAsync(url);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new List<AwEvent>();
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var events = new List<AwEvent>(doc.RootElement.GetArrayLength());
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var ts = el.GetProperty("timestamp").GetDateTimeOffset();
            var dur = el.TryGetProperty("duration", out var d) ? d.GetDouble() : 0;
            var data = el.TryGetProperty("data", out var dt) ? dt.Clone() : default;
            events.Add(new AwEvent(ts, dur, data));
        }
        return events;
    }
}
