using Tracker.Shared.Aw;

namespace Tracker.Shared.Storage;

/// <summary>
/// The event-store contract consumed by the daemon's internal callers (rules engine,
/// Claude module, browser endpoint, reports, coach). Signatures mirror AwClient so the
/// aw-server → own-storage swap is a type change only (plan docs/PLAN-2026-07-10).
///
/// ASYNC-ONLY BY DESIGN: the daemon hosts WPF popup/toast threads and the supervisor is
/// WinForms STA — never call these with .Result/.Wait() from a UI thread; storage I/O
/// plus the internal write mutex would freeze the UI. Fire-and-forget or hop to the
/// thread pool instead.
/// </summary>
public interface IEventStore
{
    /// <summary>Creates the bucket if missing. Returns true when created, false when it already existed.</summary>
    Task<bool> EnsureBucketAsync(string bucketId, string type, CancellationToken ct = default);

    /// <summary>
    /// aw-server heartbeat semantics: merges with the bucket's last event when the data is
    /// identical (canonical JSON) and the heartbeat starts within pulsetime of the last
    /// event's end — extending the event's end to max(old end, new end). Otherwise inserts.
    /// </summary>
    Task HeartbeatAsync(
        string bucketId,
        IReadOnlyDictionary<string, object?> data,
        double pulsetimeSeconds,
        DateTimeOffset? timestamp = null,
        double durationSeconds = 0,
        CancellationToken ct = default);

    /// <summary>Events with start timestamp in [start, end], newest first; missing bucket yields an empty list.</summary>
    Task<List<AwEvent>> GetEventsRangeAsync(
        string bucketId, DateTimeOffset start, DateTimeOffset end, int limit = -1, CancellationToken ct = default);
}
