using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Tracker.Daemon.Engine;
using Tracker.Daemon.Popup;
using Tracker.Shared.Aw;
using Tracker.Shared.Config;
using Tracker.Shared.Storage;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Coach;

/// <summary>
/// Coach v0 (local, no LLM): configurable rules over the live engine snapshot, delivered
/// as calm toasts. Design constraints (validated by interruption research):
///  - FLOW (≥ flow_minutes continuous productive on one project) → total silence;
///  - context-dependent rules deliver ONLY at natural breakpoints (app switch);
///  - global anti-spam gap + per-rule cooldown + only within working hours.
/// </summary>
public sealed class CoachEngine : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    private readonly ConfigProvider _config;
    private readonly RulesEngineService _engine;
    private readonly PopupService _popup;
    private readonly DayStateStore _days;
    private readonly string _dashboardUrl;

    // accumulators
    private double _flowMin;
    private string? _flowProject;
    private double _unprodMin;
    private double _activeRunMin;
    private double _idleMin;
    private string? _lastApp;
    private readonly Queue<DateTimeOffset> _switches = new();
    private DateTimeOffset _lastNudge = DateTimeOffset.MinValue;
    private readonly Dictionary<string, DateTimeOffset> _ruleFired = new();
    private (string Rule, string Title, string Msg, Action? Click)? _pending;

    private readonly IEventStore _store;

    public CoachEngine(ConfigProvider config, RulesEngineService engine, PopupService popup, DayStateStore days, int bridgePort, IEventStore store)
    {
        _store = store;
        _config = config;
        _engine = engine;
        _popup = popup;
        _days = days;
        _dashboardUrl = $"http://localhost:{bridgePort}";
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Log.Info("Coach engine running (v0, local rules)");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Step();
            }
            catch (Exception ex)
            {
                Log.Error("Coach tick failed: " + ex);
            }
            try
            {
                await Task.Delay(Tick, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Step()
    {
        var cfg = _config.Current;
        if (!cfg.Coach.Enabled) return;
        var snap = _engine.Snapshot;
        var now = DateTimeOffset.Now;
        var dt = Tick.TotalMinutes;

        // --- accumulators -----------------------------------------------------
        var breakpoint = false;
        if (snap is { Active: true })
        {
            _idleMin = 0;
            _activeRunMin += dt;
            if (snap.Class == "productive" && snap.Project is not null && snap.Project == _flowProject)
            {
                _flowMin += dt;
            }
            else if (snap.Class == "productive" && snap.Project is not null)
            {
                _flowProject = snap.Project;
                _flowMin = dt;
            }
            else
            {
                _flowMin = 0;
                _flowProject = snap.Class == "productive" ? _flowProject : null;
            }

            _unprodMin = snap.Class == "unproductive" ? _unprodMin + dt : 0;

            if (_lastApp is not null && !snap.App.Equals(_lastApp, StringComparison.OrdinalIgnoreCase))
            {
                breakpoint = true;
                _switches.Enqueue(now);
            }
            _lastApp = snap.App;
        }
        else
        {
            _idleMin += dt;
            _unprodMin = 0;
            _flowMin = 0;
            if (_idleMin >= 5) _activeRunMin = 0; // ≥5 min pauză = pauză reală
        }
        while (_switches.Count > 0 && (now - _switches.Peek()).TotalMinutes > 60) _switches.Dequeue();

        // --- rituals (independent of anti-spam gating below) -------------------
        MaybeMorningIntent(cfg, snap, now);
        MaybeShutdown(cfg, snap, now);

        // --- gating -------------------------------------------------------------
        if (!InWorkHours(cfg.Profile, now)) return;
        var inFlow = _flowMin >= cfg.Coach.FlowMinutes;
        if (inFlow) { _pending = null; return; } // flow = tăcere totală
        if (InFocusInterval(cfg.Profile, now) && snap is { Class: "productive" }) return;

        // deliver a pending breakpoint-nudge
        if (_pending is not null && breakpoint && CanNudge(cfg, _pending.Value.Rule, now))
        {
            Deliver(cfg, _pending.Value, now);
            _pending = null;
            return;
        }

        // --- rules ---------------------------------------------------------------
        var candidate = EvaluateRules(cfg, snap, now);
        if (candidate is null) return;
        var immediate = candidate.Value.Rule is "unproductive" or "main_not_started";
        if (!CanNudge(cfg, candidate.Value.Rule, now)) return;
        if (immediate || breakpoint)
        {
            Deliver(cfg, candidate.Value, now);
        }
        else
        {
            _pending = candidate.Value; // așteaptă un breakpoint natural
        }
    }

    private (string Rule, string Title, string Msg, Action? Click)? EvaluateRules(TrackerConfig cfg, EngineSnapshot? snap, DateTimeOffset now)
    {
        var c = cfg.Coach;
        var day = _days.Today();
        var goal = NearestGoal(cfg.Profile, now);

        if (c.RuleUnproductive && _unprodMin >= c.UnproductiveMinutes)
        {
            var msg = Template(cfg, "unproductive", new()
            {
                ["minutes"] = ((int)_unprodMin).ToString(),
                ["goal"] = goal?.Title ?? "obiectivul tău",
                ["days"] = goal is not null ? DaysLeft(goal, now) : "",
                ["why"] = cfg.Profile.Why,
            });
            return ("unproductive", "Coach", msg, OpenDashboard());
        }

        if (c.RuleContextSwitching && _switches.Count > c.MaxSwitchesPerHour && _activeRunMin >= 30)
        {
            var msg = Template(cfg, "switching", new() { ["count"] = _switches.Count.ToString() });
            return ("switching", "Coach", msg, null);
        }

        if (c.RuleMainNotStarted && TimeSpan.TryParse(c.MainProjectCheckAt, out var checkAt)
            && now.TimeOfDay >= checkAt && day.Priorities.Count > 0 && !day.Priorities[0].Done)
        {
            var p0 = day.Priorities[0];
            var touched = p0.Project.Length > 0 && ProjectMinutesToday(p0.Project) >= 5;
            if (!touched)
            {
                var msg = Template(cfg, "main_not_started", new() { ["priority"] = p0.Text });
                return ("main_not_started", "Prioritatea #1", msg, OpenDashboard());
            }
        }

        if (c.RuleNoBreak && _activeRunMin >= c.NoBreakHours * 60)
        {
            var msg = Template(cfg, "no_break", new() { ["hours"] = (_activeRunMin / 60).ToString("0.0") });
            return ("no_break", "Pauză", msg, null);
        }

        if (c.RuleDeadlineDrift && goal is { Project.Length: > 0 })
        {
            var days = (DateTime.Parse(goal.Deadline) - now.Date).TotalDays;
            if (days <= c.DeadlineDriftDays)
            {
                var total = TotalActiveMinutesToday();
                var onGoal = ProjectMinutesToday(goal.Project);
                if (total >= 60 && onGoal < total * 0.2)
                {
                    var msg = Template(cfg, "deadline_drift", new()
                    {
                        ["goal"] = goal.Title,
                        ["days"] = ((int)Math.Max(0, days)).ToString(),
                        ["project"] = goal.Project,
                    });
                    return ("deadline_drift", "Deadline aproape", msg, OpenDashboard());
                }
            }
        }
        return null;
    }

    // --- rituals ---------------------------------------------------------------

    private void MaybeMorningIntent(TrackerConfig cfg, EngineSnapshot? snap, DateTimeOffset now)
    {
        if (snap is not { Active: true } || !InWorkHours(cfg.Profile, now)) return;
        var day = _days.Today();
        if (day.IntentPromptShown) return;
        _days.Mutate(s => s.IntentPromptShown = true);
        var hint = YesterdayPlan();
        _popup.ShowToast(
            "Bună dimineața ☀",
            "Ce vrei să termini azi? Setează-ți top 3 priorități." + (hint.Length > 0 ? $"\nIeri ți-ai propus: {hint}" : ""),
            cfg.Coach.ToastSeconds + 6,
            OpenDashboard());
    }

    private void MaybeShutdown(TrackerConfig cfg, EngineSnapshot? snap, DateTimeOffset now)
    {
        if (snap is not { Active: true }) return;
        if (!TimeSpan.TryParse(cfg.Profile.WorkEnd, out var end) || now.TimeOfDay < end) return;
        var day = _days.Today();
        if (day.ShutdownPromptShown) return;
        _days.Mutate(s => s.ShutdownPromptShown = true);
        var done = day.Priorities.Count(p => p.Done);
        _popup.ShowToast(
            "Închide ziua 🌙",
            $"Program încheiat. Priorități bifate: {done}/{day.Priorities.Count}. Deschide Jurnalul pentru review-ul de 2 minute.",
            cfg.Coach.ToastSeconds + 6,
            OpenJournal());
    }

    // --- helpers -----------------------------------------------------------------

    private bool CanNudge(TrackerConfig cfg, string rule, DateTimeOffset now)
    {
        if ((now - _lastNudge).TotalMinutes < cfg.Coach.MinMinutesBetweenNudges) return false;
        if (_ruleFired.TryGetValue(rule, out var at) && (now - at).TotalMinutes < cfg.Coach.RuleCooldownMinutes) return false;
        return true;
    }

    private void Deliver(TrackerConfig cfg, (string Rule, string Title, string Msg, Action? Click) n, DateTimeOffset now)
    {
        _lastNudge = now;
        _ruleFired[n.Rule] = now;
        _popup.ShowToast(n.Title, n.Msg, cfg.Coach.ToastSeconds, n.Click);
        _days.Mutate(s => s.Nudges.Add(new NudgeRecord { Time = now.ToString("HH:mm"), Rule = n.Rule, Message = n.Msg }));
        Log.Info($"Coach nudge [{n.Rule}]: {n.Msg}");
    }

    private Action OpenDashboard() => () => OpenUrl(_dashboardUrl);
    private Action OpenJournal() => () => OpenUrl(_dashboardUrl + "/#journal");

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // browser launch is best-effort
        }
    }

    private static bool InWorkHours(ProfileConfig p, DateTimeOffset now)
    {
        if (!p.WorkWeekends && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        if (!TimeSpan.TryParse(p.WorkStart, out var s) || !TimeSpan.TryParse(p.WorkEnd, out var e)) return true;
        var t = now.TimeOfDay;
        if (t < s || t >= e) return false;
        var lunch = p.Lunch.Split('-');
        if (lunch.Length == 2 && TimeSpan.TryParse(lunch[0], out var ls) && TimeSpan.TryParse(lunch[1], out var le)
            && t >= ls && t < le)
            return false; // pauza de prânz = fără nudge-uri
        return true;
    }

    private static bool InFocusInterval(ProfileConfig p, DateTimeOffset now)
    {
        foreach (var iv in p.FocusIntervals)
        {
            var parts = iv.Split('-');
            if (parts.Length == 2 && TimeSpan.TryParse(parts[0], out var s) && TimeSpan.TryParse(parts[1], out var e)
                && now.TimeOfDay >= s && now.TimeOfDay < e)
                return true;
        }
        return false;
    }

    private static GoalItem? NearestGoal(ProfileConfig p, DateTimeOffset now) =>
        p.ObjectiveList
            .Where(g => g.Deadline.Length > 0 && DateTime.TryParse(g.Deadline, out var d) && d.Date >= now.Date)
            .OrderBy(g => DateTime.Parse(g.Deadline))
            .FirstOrDefault()
        ?? p.ObjectiveList.FirstOrDefault();

    private static string DaysLeft(GoalItem g, DateTimeOffset now) =>
        g.Deadline.Length > 0 && DateTime.TryParse(g.Deadline, out var d)
            ? ((int)Math.Max(0, (d.Date - now.Date).TotalDays)).ToString()
            : "";

    private string YesterdayPlan()
    {
        var y = _days.Load(DateTimeOffset.Now.AddDays(-1).ToString("yyyy-MM-dd"));
        return y.TomorrowPlan.Trim();
    }

    // --- today totals from the derived project bucket (cheap, cached) --------------

    private DateTimeOffset _totalsAt = DateTimeOffset.MinValue;
    private Dictionary<string, double> _projMin = new(StringComparer.OrdinalIgnoreCase);
    private double _totalMin;

    private void RefreshTotals()
    {
        if ((DateTimeOffset.UtcNow - _totalsAt).TotalMinutes < 3) return;
        _totalsAt = DateTimeOffset.UtcNow;
        try
        {
            var cfg = _config.Current;
            var host = cfg.Server.ResolveBucketHost();
            // M5 (2026-07-12): direct din store-ul propriu, fără HTTP (BackgroundService
            // thread — GetResult() e sigur aici, nu există sync context de blocat)
            var start = new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset);
            var events = _store.GetEventsRangeAsync(AwBuckets.Project(host), start, DateTimeOffset.Now)
                .GetAwaiter().GetResult();
            var proj = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double total = 0;
            foreach (var e in events)
            {
                // the store returns range-OVERLAPPING events — count only today's share of
                // a span that started before midnight
                var dur = e.Timestamp < start
                    ? Math.Max(0, e.Duration - (start - e.Timestamp).TotalSeconds)
                    : e.Duration;
                total += dur;
                if (e.Data.ValueKind == System.Text.Json.JsonValueKind.Object
                    && e.Data.TryGetProperty("project", out var pv)
                    && pv.ValueKind == System.Text.Json.JsonValueKind.String && pv.GetString() is { Length: > 0 } p)
                    proj[p] = proj.GetValueOrDefault(p) + dur;
            }
            _projMin = proj.ToDictionary(kv => kv.Key, kv => kv.Value / 60, StringComparer.OrdinalIgnoreCase);
            _totalMin = total / 60;
        }
        catch
        {
            // aw-server down — keep stale totals
        }
    }

    private double ProjectMinutesToday(string project)
    {
        RefreshTotals();
        return _projMin.GetValueOrDefault(project);
    }

    private double TotalActiveMinutesToday()
    {
        RefreshTotals();
        return _totalMin;
    }

    // --- message templates per motivation style ------------------------------------

    private string Template(TrackerConfig cfg, string rule, Dictionary<string, string> vars)
    {
        var styles = cfg.Profile.MotivationStyles.Count > 0 ? cfg.Profile.MotivationStyles : new List<string> { "coach" };
        var style = styles[Math.Abs(Environment.TickCount) % styles.Count];
        var text = (rule, style) switch
        {
            ("unproductive", "funny") => "Hei 😄 Internetul nu pleacă nicăieri. {goal} însă are {days} zile rămase. Revenim?",
            ("unproductive", "direct") => "{minutes} minute neproductive. {goal} nu se face singur. Înapoi la treabă.",
            ("unproductive", "calm") => "Ai luat o pauză de {minutes} minute. Când ești gata, {goal} te așteaptă.",
            ("unproductive", "data_driven") => "{minutes} min neproductive azi. Obiectiv activ: {goal} ({days} zile rămase).",
            ("unproductive", _) => "Ai spus că vrei: {goal}. Ultimele {minutes} minute au fost în altă parte{days_suffix}. Revenim?",

            ("switching", "data_driven") => "{count} schimbări de aplicație în ultima oră. Concentrarea se reconstruiește în ~23 min după fiecare.",
            ("switching", _) => "Multe schimbări de context în ultima oră ({count}). Alege UN lucru pentru următoarele 25 de minute.",

            ("main_not_started", "direct") => "Prioritatea #1 — „{priority}” — încă neatinsă. Începe acum cu 10 minute.",
            ("main_not_started", _) => "Ți-ai propus azi: „{priority}”. Încă n-ai început — un start de 10 minute?",

            ("no_break", "calm") => "Lucrezi de {hours}h fără pauză. 5 minute de mers te resetează.",
            ("no_break", _) => "{hours}h fără pauză. Ia 5 minute — te întorci mai bun.",

            ("deadline_drift", _) => "„{goal}” are deadline în {days} zile, dar azi timpul s-a dus în altă parte. Măcar o oră pe {project}?",
            _ => "{goal}",
        };
        vars["days_suffix"] = vars.TryGetValue("days", out var dd) && dd.Length > 0 ? $" (deadline în {dd} zile)" : "";
        foreach (var (k, v) in vars) text = text.Replace("{" + k + "}", v);
        return text;
    }
}
