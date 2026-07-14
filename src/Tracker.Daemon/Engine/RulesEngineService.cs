using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Tracker.Daemon.State;
using Tracker.Shared.Aw;
using Tracker.Shared.Config;
using Tracker.Shared.Storage;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Engine;

/// <summary>Live result of the last engine tick — consumed by /state, the popup (M3) and the dashboard.</summary>
public sealed record EngineSnapshot(
    string App,
    string Title,
    string? Project,
    string Class,
    string? MatchedRule,
    bool ExceptionApplied,
    bool Active,
    bool Afk,
    bool VideoOverride,
    string? Profile,
    DateTimeOffset At);

/// <summary>
/// The rules engine tick (architecture §1.3): every second, combine window mirror +
/// browser state → attribution + classification + video rule, and emit the derived
/// tracker-project bucket (already AFK/video-corrected, §5.4) every 5 s while active.
/// </summary>
public sealed class RulesEngineService : BackgroundService
{
    private static readonly TimeSpan WindowFreshness = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BrowserFreshness = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ProjectHeartbeatEvery = TimeSpan.FromSeconds(5);
    private const double ProjectPulsetime = 15;

    private readonly ConfigProvider _config;
    private readonly WindowStateStore _window;
    private readonly BrowserStateStore _browser;
    private readonly IEventStore _store;
    private readonly string _host;
    private readonly AttributionEngine _attribution = new();
    private readonly ClassificationEngine _classification = new();

    private volatile EngineSnapshot? _snapshot;
    private DateTimeOffset _lastProjectHeartbeat;

    public RulesEngineService(
        ConfigProvider config, WindowStateStore window, BrowserStateStore browser,
        IEventStore store, string host)
    {
        _config = config;
        _window = window;
        _browser = browser;
        _store = store;
        _host = host;
    }

    public EngineSnapshot? Snapshot => _snapshot;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = EnsureBucketsAsync(ct); // in parallel — ticks start now, heartbeats queue until ready
        Log.Info("Rules engine running (1s tick, project heartbeat every 5s while active)");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Engine tick failed: " + ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task EnsureBucketsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _store.EnsureBucketAsync(AwBuckets.Project(_host), AwBuckets.ProjectType, ct);
                await _store.EnsureBucketAsync(AwBuckets.Web(_host), AwBuckets.WebType, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // shutdown
            }
            catch (Exception ex)
            {
                // same zombie pattern as the watcher's bucket loop: this runs fire-and-forget,
                // so an unexpected exception would end it silently and every project/web
                // heartbeat would then throw KeyNotFound (unobserved) forever
                Log.Warn($"bucket creation failed ({ex.GetType().Name}: {ex.Message}), retrying in 10s ...");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        var now = DateTimeOffset.UtcNow;
        var (win, lastUpdate) = _window.Snapshot();

        if (win is null || now - lastUpdate > WindowFreshness)
        {
            _snapshot = null; // watcher down/stale — no attribution, no popup triggers
            return;
        }

        var isBrowser = AttributionEngine.IsBrowser(cfg, win.App);
        var browser = isBrowser ? _browser.BestFor(win.Title, win.App, BrowserFreshness) : null;

        var project = _attribution.Attribute(cfg, win.App, win.Title, win.Aumid, browser?.Url, browser?.Profile, now);
        var cls = _classification.Classify(cfg, win.App, win.Title, browser?.Url, browser?.Channel);

        // atribuirile pe ZI se aplică și LIVE, nu doar retroactiv în rapoarte: „WhatsApp
        // productiv azi" trebuie să oprească și popup-ul, nu doar să recoloreze raportul
        var (dayProj, dayCls) = DayOverride(cfg, now, win.App, win.Title, browser?.Url, isBrowser);
        if (dayProj is not null) project = dayProj;
        if (dayCls is not null) cls = cls with { Class = dayCls };

        // decision #6: audible tab + browser focused ⇒ active even with zero input
        var videoOverride = cfg.Video.AudibleFocusedCountsActive && isBrowser && _browser.AnyAudible(BrowserFreshness);
        var active = !win.Afk || videoOverride;

        _snapshot = new EngineSnapshot(
            win.App, win.Title, project, cls.Class, cls.MatchedRule, cls.ExceptionApplied,
            active, win.Afk, videoOverride, browser?.Profile, now);

        // backward clock step: a cadence mark left in the "future" would silence the
        // project bucket for the whole excursion — reset it so heartbeats keep flowing
        if (_lastProjectHeartbeat > now) _lastProjectHeartbeat = default;
        if (active && now - _lastProjectHeartbeat >= ProjectHeartbeatEvery)
        {
            var data = new Dictionary<string, object?>
            {
                ["project"] = project ?? "",
                ["class"] = cls.Class,
            };
            await _store.HeartbeatAsync(AwBuckets.Project(_host), data, ProjectPulsetime, ct: ct);
            _lastProjectHeartbeat = now;
        }
    }

    /// <summary>Atribuirea pe zi pentru MOMENTUL curent, cu aceeași precedență ca în
    /// raport (domain înaintea app; intervalul bate intrarea pe toată ziua pe câmpurile
    /// pe care le definește). Întoarce (null, null) când nu se aplică nimic azi.</summary>
    private static (string? Project, string? Class) DayOverride(
        TrackerConfig cfg, DateTimeOffset now, string app, string title, string? url, bool isBrowser)
    {
        if (cfg.Assignments.Count == 0) return (null, null);
        var local = now.ToLocalTime();
        var dayKey = local.ToString("yyyy-MM-dd");
        AssignmentConfig? dayHit = null, spanHit = null;
        foreach (var pass in new[] { "domain", "app" })
        {
            foreach (var a in cfg.Assignments)
            {
                if (a.Date != dayKey || a.Match != pass) continue;
                var matches = pass == "app"
                    ? string.Equals(app, a.Value, StringComparison.OrdinalIgnoreCase)
                    : ClassificationEngine.MatchesDomain(a.Value, url, title, titleFallback: isBrowser);
                if (!matches) continue;
                if (a.HasInterval)
                {
                    var f = Report.ReportService.LocalInstant(local.Date, a.From);
                    var t = Report.ReportService.LocalInstant(local.Date, a.To);
                    if (now >= f && now < t) spanHit ??= a;
                }
                else
                {
                    dayHit ??= a;
                }
            }
        }
        string? proj = null, cls = null;
        if (dayHit is not null)
        {
            if (dayHit.Project.Length > 0) proj = dayHit.Project;
            if (dayHit.Class.Length > 0) cls = dayHit.Class;
        }
        if (spanHit is not null)
        {
            if (spanHit.Project.Length > 0) proj = spanHit.Project;
            if (spanHit.Class.Length > 0) cls = spanHit.Class;
        }
        return (proj, cls);
    }
}
