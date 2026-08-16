using System.IO;
using System.Text.Json;
using Tracker.Daemon.Storage;
using Tracker.Shared.Aw;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// M1 gate (plan docs/PLAN-2026-07-10-remove-activitywatch.md): the merge semantics every
/// dashboard duration depends on. Mirrors aw-server heartbeat behavior: identical canonical
/// data + within pulsetime of last end ⇒ extend to max end; anything else ⇒ new event.
/// </summary>
public sealed class EventStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir;
    private readonly EventStore _store;
    private const string Bucket = "test-bucket_HOST";

    public EventStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tracker-tests", Guid.NewGuid().ToString("N"));
        _store = new EventStore(Path.Combine(_dir, "events.db"), "tracker-tests", "HOST");
        _store.EnsureBucketAsync(Bucket, "test").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static Dictionary<string, object?> Data(string app = "chrome.exe", string title = "x") =>
        new() { ["app"] = app, ["title"] = title };

    private Task Hb(double atSeconds, IReadOnlyDictionary<string, object?> data, double pulse = 10, double duration = 0) =>
        _store.HeartbeatAsync(Bucket, data, pulse, T0.AddSeconds(atSeconds), duration);

    private List<AwEvent> All() =>
        _store.GetEventsRangeAsync(Bucket, T0.AddDays(-1), T0.AddDays(1)).GetAwaiter().GetResult();

    [Fact]
    public async Task IdenticalDataWithinPulsetime_MergesIntoOneEvent()
    {
        await Hb(0, Data());
        await Hb(5, Data());
        await Hb(9, Data());
        var events = All();
        var e = Assert.Single(events);
        Assert.Equal(T0, e.Timestamp);
        Assert.Equal(9, e.Duration, 3); // end extended to the last heartbeat
    }

    [Fact]
    public async Task GapBeyondPulsetime_Splits()
    {
        await Hb(0, Data());
        await Hb(20, Data()); // pulse 10, gap 20 → new event
        Assert.Equal(2, All().Count);
    }

    [Fact]
    public async Task DifferentData_Splits()
    {
        await Hb(0, Data(title: "a"));
        await Hb(2, Data(title: "b"));
        Assert.Equal(2, All().Count);
    }

    [Fact]
    public async Task DataKeyOrder_DoesNotAffectEquality()
    {
        await Hb(0, new Dictionary<string, object?> { ["app"] = "x", ["title"] = "y" });
        await Hb(3, new Dictionary<string, object?> { ["title"] = "y", ["app"] = "x" });
        Assert.Single(All());
    }

    [Fact]
    public async Task AfkBackfill_PastTimestampWithDuration_ExtendsToMaxEnd()
    {
        // watcher AFK pattern: heartbeat at detection time, then a backfill with the
        // explicit onset timestamp + full idle duration overlapping the previous event
        await Hb(0, Data("afk", "afk"), pulse: 195);
        await Hb(30, Data("afk", "afk"), pulse: 195);
        await Hb(10, Data("afk", "afk"), pulse: 195, duration: 60); // past ts, end = 70 > current end 30
        var e = Assert.Single(All());
        Assert.Equal(70, e.Duration, 3);
    }

    [Fact]
    public async Task BackfillEndInsidePreviousEvent_DoesNotShrink()
    {
        await Hb(0, Data(), duration: 50);
        await Hb(10, Data(), duration: 5); // end 15 < existing end 50 → max-end keeps 50
        var e = Assert.Single(All());
        Assert.Equal(50, e.Duration, 3);
    }

    [Fact]
    public async Task ClockStepBack_DoesNotMutateHistoryBackwards()
    {
        await Hb(10, Data());
        await Hb(2, Data()); // older ts, same data → separate insert, guard refuses the merge
        var events = All();
        Assert.Equal(2, events.Count);
        Assert.Equal(T0.AddSeconds(10), events[0].Timestamp); // newest first, untouched
        Assert.Equal(T0.AddSeconds(2), events[1].Timestamp);
    }

    [Fact]
    public async Task NegativeDuration_ClampedToZero()
    {
        await Hb(0, Data(), duration: -5);
        var e = Assert.Single(All());
        Assert.Equal(0, e.Duration, 3);
    }

    [Fact]
    public async Task RangeQuery_StartContainment_DescOrder_Limit()
    {
        await Hb(0, Data(title: "a"));
        await Hb(60, Data(title: "b"));
        await Hb(120, Data(title: "c"));

        // start-containment boundaries are inclusive
        var mid = await _store.GetEventsRangeAsync(Bucket, T0.AddSeconds(60), T0.AddSeconds(120));
        Assert.Equal(2, mid.Count);
        Assert.True(mid[0].Timestamp > mid[1].Timestamp); // newest first

        var limited = await _store.GetEventsRangeAsync(Bucket, T0.AddDays(-1), T0.AddDays(1), limit: 1);
        var only = Assert.Single(limited);
        Assert.Equal(T0.AddSeconds(120), only.Timestamp); // limit keeps the most recent
    }

    [Fact]
    public async Task MissingBucket_ReadsEmpty_HeartbeatThrows_EnsureCreatesOnce()
    {
        Assert.Empty(await _store.GetEventsRangeAsync("nope_HOST", T0, T0.AddDays(1)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _store.HeartbeatAsync("nope_HOST", Data(), 10, T0));
        Assert.True(await _store.EnsureBucketAsync("nope_HOST", "test"));  // created
        Assert.False(await _store.EnsureBucketAsync("nope_HOST", "test")); // already there → shim's 304
    }

    [Fact]
    public async Task ConcurrentIdenticalHeartbeats_NeverSplitOrDuplicate()
    {
        var data = Data("claude.exe", "sessions");
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
                await _store.HeartbeatAsync(Bucket, data, 10, T0.AddSeconds(1)); // same ts on purpose
        }));
        await Task.WhenAll(tasks);
        Assert.Single(All()); // without the write gate this races into duplicates
    }

    [Fact]
    public async Task DuplicateDelivery_IsIdempotent()
    {
        // ResilientAwClient can re-send a heartbeat after a timeout that actually landed —
        // applying every heartbeat twice must yield the same store as applying it once
        var rnd = new Random(42);
        var titles = new[] { "a", "b" };
        var t = 0.0;
        for (var i = 0; i < 60; i++)
        {
            t += rnd.NextDouble() * 15;
            var data = Data(title: titles[rnd.Next(2)]);
            var dur = rnd.NextDouble() * 3;
            await Hb(t, data, pulse: 10, duration: dur);
            await Hb(t, data, pulse: 10, duration: dur); // duplicate delivery
        }
        var withDupes = All().Select(e => (e.Timestamp, e.Duration, e.Data.GetRawText())).ToList();

        using var fresh = new EventStore(Path.Combine(_dir, "fresh.db"), "tracker-tests", "HOST");
        await fresh.EnsureBucketAsync(Bucket, "test");
        rnd2(fresh);
        var clean = fresh.GetEventsRangeAsync(Bucket, T0.AddDays(-1), T0.AddDays(1)).GetAwaiter().GetResult()
            .Select(e => (e.Timestamp, e.Duration, e.Data.GetRawText())).ToList();

        Assert.Equal(clean, withDupes);

        void rnd2(EventStore s)
        {
            var r = new Random(42);
            var tt = 0.0;
            for (var i = 0; i < 60; i++)
            {
                tt += r.NextDouble() * 15;
                var data = Data(title: titles[r.Next(2)]);
                var dur = r.NextDouble() * 3;
                s.HeartbeatAsync(Bucket, data, 10, T0.AddSeconds(tt), dur).GetAwaiter().GetResult();
            }
        }
    }

    [Fact]
    public async Task LastEventCache_SurvivesReopen_MergesAcrossRestart()
    {
        await Hb(0, Data());
        var path = _store.DbPath;
        _store.Dispose();

        using var reopened = new EventStore(path, "tracker-tests", "HOST");
        await reopened.HeartbeatAsync(Bucket, Data(), 10, T0.AddSeconds(5));
        var events = await reopened.GetEventsRangeAsync(Bucket, T0.AddDays(-1), T0.AddDays(1));
        var e = Assert.Single(events); // merged with the pre-restart event (lazy cache reload)
        Assert.Equal(5, e.Duration, 3);
    }

    [Fact]
    public void MicrosecondRoundTrip_IsExact()
    {
        var dto = DateTimeOffset.Parse("2026-07-10T14:29:03.015437+00:00");
        Assert.Equal(dto, EventStore.FromUs(EventStore.ToUs(dto)));
    }

    [Fact]
    public void CanonicalJson_SortsKeys_PreservesNumberText()
    {
        using var doc = JsonDocument.Parse("""{"b":1.50000,"a":"x","c":{"z":true,"y":[2,1]}}""");
        Assert.Equal("""{"a":"x","b":1.50000,"c":{"y":[2,1],"z":true}}""", JsonCanonical.Canonicalize(doc.RootElement));
    }
}
