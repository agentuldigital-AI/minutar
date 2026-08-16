using Tracker.Shared.Aw;
using Tracker.Shared.Logging;
using Tracker.Shared.Storage;

namespace Tracker.Daemon.Storage;

/// <summary>
/// Shadow-period wrapper (cutover 2026-07-12, docs/CUTOVER-2026-07-12.md): READS come from
/// the own EventStore — the new system is live — while WRITES also get forwarded to the old
/// aw-server so both stay comparable until the final re-verification. Forward failures never
/// break the local write (the shadow is best-effort; a gap there only weakens the diff).
/// Retire by clearing [storage] tee_aw_url — this wrapper is then never constructed.
/// </summary>
public sealed class TeeEventStore : IEventStore
{
    private readonly EventStore _store;
    private readonly AwClient _aw;
    private long _forwardFailures;
    private DateTimeOffset _lastFailLog;

    public TeeEventStore(EventStore store, AwClient aw)
    {
        _store = store;
        _aw = aw;
    }

    public async Task<bool> EnsureBucketAsync(string bucketId, string type, CancellationToken ct = default)
    {
        var created = await _store.EnsureBucketAsync(bucketId, type, ct);
        try
        {
            await _aw.EnsureBucketAsync(bucketId, type, ct);
        }
        catch (Exception ex)
        {
            LogFail(ex);
        }
        return created;
    }

    public async Task HeartbeatAsync(
        string bucketId,
        IReadOnlyDictionary<string, object?> data,
        double pulsetimeSeconds,
        DateTimeOffset? timestamp = null,
        double durationSeconds = 0,
        CancellationToken ct = default)
    {
        // pin the timestamp once so both stores record the exact same instant
        var ts = timestamp ?? DateTimeOffset.UtcNow;
        await _store.HeartbeatAsync(bucketId, data, pulsetimeSeconds, ts, durationSeconds, ct);
        try
        {
            await _aw.HeartbeatAsync(bucketId, data, pulsetimeSeconds, ts, durationSeconds, ct);
        }
        catch (Exception ex)
        {
            LogFail(ex);
        }
    }

    public Task<List<AwEvent>> GetEventsRangeAsync(
        string bucketId, DateTimeOffset start, DateTimeOffset end, int limit = -1, CancellationToken ct = default)
        => _store.GetEventsRangeAsync(bucketId, start, end, limit, ct);

    private void LogFail(Exception ex)
    {
        _forwardFailures++;
        if (DateTimeOffset.UtcNow - _lastFailLog > TimeSpan.FromMinutes(1))
        {
            Log.Warn($"[shadow] aw forward failed ({_forwardFailures} total): {ex.Message}");
            _lastFailLog = DateTimeOffset.UtcNow;
        }
    }
}
