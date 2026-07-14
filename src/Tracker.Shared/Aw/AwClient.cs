using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tracker.Shared.Aw;

/// <summary>One ActivityWatch event (timestamp UTC, duration seconds, raw data).</summary>
public sealed record AwEvent(DateTimeOffset Timestamp, double Duration, JsonElement Data);

/// <summary>
/// Minimal ActivityWatch REST client (localhost:5600, no auth in v0.13.x).
/// Talks ONLY to /api/0/ — never patches AW internals (repo convention).
/// </summary>
public sealed class AwClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly HttpClient _http;
    private readonly string _clientName;
    private readonly string _hostname;

    public AwClient(string baseUrl, string clientName, string hostname, TimeSpan? timeout = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/0/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        };
        _clientName = clientName;
        _hostname = hostname;
    }

    public async Task<string> GetInfoAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("info", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Creates the bucket if missing; aw-server answers 304 when it already exists.</summary>
    public async Task EnsureBucketAsync(string bucketId, string type, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { client = _clientName, type, hostname = _hostname }, JsonOpts);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"buckets/{Uri.EscapeDataString(bucketId)}", content, ct);
        if (resp.StatusCode == HttpStatusCode.NotModified) return;
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// POST /heartbeat?pulsetime=N — aw-server merges consecutive events with identical
    /// data within N seconds (THE core storage pattern, research §4).
    /// </summary>
    public async Task HeartbeatAsync(
        string bucketId,
        IReadOnlyDictionary<string, object?> data,
        double pulsetimeSeconds,
        DateTimeOffset? timestamp = null,
        double durationSeconds = 0,
        CancellationToken ct = default)
    {
        var ev = new
        {
            timestamp = (timestamp ?? DateTimeOffset.UtcNow)
                .ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            duration = durationSeconds,
            data,
        };
        var body = JsonSerializer.Serialize(ev, JsonOpts);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var pulse = pulsetimeSeconds.ToString(CultureInfo.InvariantCulture);
        var resp = await _http.PostAsync(
            $"buckets/{Uri.EscapeDataString(bucketId)}/heartbeat?pulsetime={pulse}", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<string> GetEventsAsync(string bucketId, int limit = 10, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"buckets/{Uri.EscapeDataString(bucketId)}/events?limit={limit}", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Typed events in a time range; a missing bucket yields an empty list.</summary>
    public async Task<List<AwEvent>> GetEventsRangeAsync(
        string bucketId, DateTimeOffset start, DateTimeOffset end, int limit = -1, CancellationToken ct = default)
    {
        var url = $"buckets/{Uri.EscapeDataString(bucketId)}/events" +
                  $"?start={Uri.EscapeDataString(start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))}" +
                  $"&end={Uri.EscapeDataString(end.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))}" +
                  $"&limit={limit}";
        var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return new List<AwEvent>();
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
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

    public void Dispose() => _http.Dispose();
}
