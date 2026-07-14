using System.Collections.Concurrent;
using System.Net;
using Tracker.Shared.Logging;

namespace Tracker.Shared.Aw;

/// <summary>
/// Wraps <see cref="AwClient"/> with a bounded in-memory retry queue (architecture §2).
/// Hardened per the M1 tournament review:
///  - heartbeats queue from process start (owner may gate direct sends until buckets exist);
///  - 4xx responses are NOT "server down": logged, retried a few times, then dropped, and
///    <see cref="OnClientError"/> lets the owner re-ensure buckets;
///  - the element being sent sits in an in-flight slot so drop-oldest can never lose it;
///  - a poison guard drops any element that keeps failing, instead of blocking the queue;
///  - down/up transitions are logged once, not per heartbeat.
/// </summary>
public sealed class ResilientAwClient : IDisposable
{
    private sealed record QueuedHeartbeat(
        string BucketId,
        IReadOnlyDictionary<string, object?> Data,
        double PulsetimeSeconds,
        DateTimeOffset Timestamp,
        double DurationSeconds);

    private readonly AwClient _inner;
    private readonly ConcurrentQueue<QueuedHeartbeat> _queue = new();
    private readonly int _capacity;
    private readonly Timer _flushTimer;
    private int _flushing;
    private QueuedHeartbeat? _inFlight;
    private int _inFlightFailures;
    private volatile bool _ready;
    private volatile bool _serverDown;

    private const int ClientErrorMaxRetries = 3;
    private const int TransientMaxRetries = 30; // ~5 min at the 10s flush cadence

    /// <summary>Fired on HTTP 4xx (e.g. bucket deleted) so the owner can re-ensure buckets.</summary>
    public Func<Task>? OnClientError { get; set; }

    public ResilientAwClient(AwClient inner, int capacity = 2000, TimeSpan? flushInterval = null, bool startReady = true)
    {
        _inner = inner;
        _capacity = capacity;
        _ready = startReady;
        var interval = flushInterval ?? TimeSpan.FromSeconds(10);
        _flushTimer = new Timer(_ => _ = FlushAsync(), null, interval, interval);
    }

    public int QueuedCount => _queue.Count + (_inFlight is null ? 0 : 1);

    /// <summary>Open the gate once the owner has ensured the buckets exist.</summary>
    public void MarkReady()
    {
        _ready = true;
        _ = FlushAsync();
    }

    public async Task HeartbeatAsync(
        string bucketId,
        IReadOnlyDictionary<string, object?> data,
        double pulsetimeSeconds,
        DateTimeOffset? timestamp = null,
        double durationSeconds = 0,
        CancellationToken ct = default)
    {
        var hb = new QueuedHeartbeat(bucketId, data, pulsetimeSeconds, timestamp ?? DateTimeOffset.UtcNow, durationSeconds);

        // preserve timestamp ordering: never bypass a draining backlog or a closed gate
        if (!_ready || _inFlight is not null || !_queue.IsEmpty)
        {
            Enqueue(hb);
            return;
        }

        try
        {
            await _inner.HeartbeatAsync(hb.BucketId, hb.Data, hb.PulsetimeSeconds, hb.Timestamp, hb.DurationSeconds, ct);
            if (_serverDown)
            {
                _serverDown = false;
                Log.Info("aw-server reachable again (direct sends resumed)");
            }
        }
        catch (HttpRequestException ex) when (IsClientError(ex))
        {
            Log.Warn($"aw-server rejected heartbeat for '{hb.BucketId}' ({(int?)ex.StatusCode}) — re-ensuring buckets");
            Enqueue(hb);
            _ = OnClientError?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!_serverDown)
            {
                _serverDown = true;
                Log.Warn("aw-server unreachable — heartbeats are being queued");
            }
            Enqueue(hb);
        }
    }

    private long _overflowDropped;
    private DateTimeOffset _lastOverflowLog;

    private void Enqueue(QueuedHeartbeat hb)
    {
        _queue.Enqueue(hb);
        while (_queue.Count > _capacity && _queue.TryDequeue(out _))
        {
            // drop oldest — bounded by design (the in-flight slot is never dropped);
            // diag 2026-07-11: a silent drop here = data loss we could never see; log it
            // rate-limited (once a minute) so a long outage doesn't flood the log
            _overflowDropped++;
            if (DateTimeOffset.UtcNow - _lastOverflowLog > TimeSpan.FromMinutes(1))
            {
                Log.Warn($"[diag] retry queue full — dropped {_overflowDropped} oldest heartbeat(s) so far");
                _lastOverflowLog = DateTimeOffset.UtcNow;
            }
        }
    }

    private async Task FlushAsync()
    {
        if (!_ready) return;
        if (_inFlight is null && _queue.IsEmpty) return;
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            var recovered = 0;
            while (true)
            {
                if (_inFlight is null)
                {
                    if (!_queue.TryDequeue(out var next)) break;
                    _inFlight = next;
                    _inFlightFailures = 0;
                }

                var hb = _inFlight;
                try
                {
                    await _inner.HeartbeatAsync(hb.BucketId, hb.Data, hb.PulsetimeSeconds, hb.Timestamp, hb.DurationSeconds);
                    _inFlight = null;
                    _inFlightFailures = 0;
                    recovered++;
                }
                catch (HttpRequestException ex) when (IsClientError(ex))
                {
                    _inFlightFailures++;
                    if (_inFlightFailures >= ClientErrorMaxRetries)
                    {
                        Log.Warn($"dropping heartbeat for '{hb.BucketId}' after {_inFlightFailures} rejections ({(int?)ex.StatusCode})");
                        _inFlight = null;
                        _inFlightFailures = 0;
                        continue;
                    }
                    _ = OnClientError?.Invoke();
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _inFlightFailures++;
                    if (_inFlightFailures >= TransientMaxRetries)
                    {
                        Log.Warn($"dropping poison heartbeat for '{hb.BucketId}' after {_inFlightFailures} transient failures");
                        _inFlight = null;
                        _inFlightFailures = 0;
                        continue;
                    }
                    break; // still down — retry on next tick
                }
            }

            if (recovered > 0)
            {
                _serverDown = false;
                Log.Info($"flushed {recovered} queued heartbeat(s), {QueuedCount} left");
            }
        }
        catch (Exception ex)
        {
            Log.Error("flush tick failed: " + ex);
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    private static bool IsClientError(HttpRequestException ex) =>
        ex.StatusCode is { } code && (int)code is >= 400 and < 500;

    public void Dispose()
    {
        _flushTimer.Dispose();
        _inner.Dispose();
    }
}
