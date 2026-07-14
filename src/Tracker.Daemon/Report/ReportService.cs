using System.Net.Http;
using System.Text.Json;
using Tracker.Daemon.Engine;
using Tracker.Shared.Aw;
using Tracker.Shared.Config;
using Tracker.Shared.Storage;

namespace Tracker.Daemon.Report;

/// <summary>
/// Server-side report computation (decision #11, architecture §1.6a): ONE classification
/// truth. The derived tracker-project bucket is already AFK/video-corrected, so per-class
/// and per-project numbers read straight from it; per-app and per-domain intersect the raw
/// window/web events with the active (project-bucket) intervals via a two-pointer sweep.
/// </summary>
public sealed class ReportService
{
    private readonly IEventStore _aw;
    private readonly ConfigProvider _config;
    private readonly string _host;

    public ReportService(IEventStore aw, ConfigProvider config, string host)
    {
        _aw = aw;
        _config = config;
        _host = host;
    }

    public async Task<object> BuildAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var projTask = _aw.GetEventsRangeAsync(AwBuckets.Project(_host), from, to, ct: ct);
        var windowTask = _aw.GetEventsRangeAsync(AwBuckets.Window(_host), from, to, ct: ct);
        var webTask = _aw.GetEventsRangeAsync(AwBuckets.Web(_host), from, to, ct: ct);
        var cworkTask = _aw.GetEventsRangeAsync(AwBuckets.ClaudeWork(_host), from, to, ct: ct);
        var cattnTask = _aw.GetEventsRangeAsync(AwBuckets.ClaudeAttention(_host), from, to, ct: ct);
        // presence/AFK context only makes sense per day — skip the extra read on long ranges
        var afkTask = to - from <= TimeSpan.FromDays(2)
            ? _aw.GetEventsRangeAsync(AwBuckets.Afk(_host), from, to, ct: ct)
            : Task.FromResult(new List<AwEvent>());
        await Task.WhenAll(projTask, windowTask, webTask, cworkTask, cattnTask, afkTask);

        // events crossing the range boundary (an AFK/video span over midnight) count only
        // their portion INSIDE [from, to] — otherwise the whole event lands in its start
        // day and the next day misses its share (days wouldn't reconcile with the week)
        var proj = ClipToRange(projTask.Result, from, to);
        var window = ClipToRange(windowTask.Result, from, to);
        var web = ClipToRange(webTask.Result, from, to);
        var cwork = ClipToRange(cworkTask.Result, from, to);
        var cattn = ClipToRange(cattnTask.Result, from, to);
        var afk = ClipToRange(afkTask.Result, from, to);
        var active = MergeIntervals(proj);

        // RETROACTIVE classification/attribution (TimeCamp model): the report re-evaluates
        // the raw window+web history with the CURRENT rules, so a reclassification click
        // updates today's numbers too — the tracker-project bucket only supplies the
        // "user was active" intervals (AFK/video-corrected).
        var slices = BuildSlices(window, web, active);
        var byClass = new Dictionary<string, double>();
        var byProject = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var byApp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var byDomain = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var classAgg = new[] { "productive", "neutral", "unproductive" }.ToDictionary(
            c => c,
            _ => (Apps: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                  Doms: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)),
            StringComparer.Ordinal);
        var projAgg = new Dictionary<string, (Dictionary<string, double> Apps, Dictionary<string, double> Doms)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in slices)
        {
            var sec = (s.End - s.Start).TotalSeconds;
            byClass[s.Cls] = byClass.GetValueOrDefault(s.Cls) + sec;
            // felia atribuită pe interval devine rând separat („zoom.exe → ClientX");
            // rândul simplu rămâne diferența pe standard (cerința userului, 2026-07-12).
            // Cheile-variantă intră în TOATE listele (Aplicații/Domenii, per clasă, per
            // proiect) — UI-ul le desenează ca sub-rânduri informative, iar acțiunile de
            // reclasificare stau doar pe rândul cu numele real.
            var appKey = s.Variant.Length > 0 ? $"{s.App} → {s.Variant}" : s.App;
            byApp[appKey] = byApp.GetValueOrDefault(appKey) + sec;
            string? domKey = null;
            if (s.Domain is not null)
            {
                domKey = s.Variant.Length > 0 ? $"{s.Domain} → {s.Variant}" : s.Domain;
                byDomain[domKey] = byDomain.GetValueOrDefault(domKey) + sec;
            }
            if (classAgg.TryGetValue(s.Cls, out var ca))
            {
                ca.Apps[appKey] = ca.Apps.GetValueOrDefault(appKey) + sec;
                if (domKey is not null) ca.Doms[domKey] = ca.Doms.GetValueOrDefault(domKey) + sec;
            }
            if (s.Project.Length > 0)
            {
                byProject[s.Project] = byProject.GetValueOrDefault(s.Project) + sec;
                if (!projAgg.TryGetValue(s.Project, out var pa))
                {
                    pa = (new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                          new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));
                    projAgg[s.Project] = pa;
                }
                pa.Apps[appKey] = pa.Apps.GetValueOrDefault(appKey) + sec;
                if (domKey is not null) pa.Doms[domKey] = pa.Doms.GetValueOrDefault(domKey) + sec;
            }
        }

        var claudeWork = NormalizeClaudeProjects(SumBy(cwork, "project"));
        var claudeAttention = NormalizeClaudeProjects(SumBy(cattn, "project"));

        var byProfile = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (e, overlap) in Intersect(web, active))
        {
            var profile = Str(e.Data, "profile");
            if (!string.IsNullOrEmpty(profile))
                byProfile[profile] = byProfile.GetValueOrDefault(profile) + overlap;
        }

        var activeSec = slices.Sum(s => (s.End - s.Start).TotalSeconds);
        var focusInfo = ComputeFocus(window, active, byClass, activeSec);

        // split: time in browsers vs local apps (domains are a SUBDIVISION of browser time,
        // so apps+domains must never be summed together)
        var browserProcs = _config.Current.Browser.Processes;
        // pe felii, nu pe byApp: cheile byApp pot purta sufix de variantă („chrome.exe → X")
        var browserSec = slices.Where(s => browserProcs.Contains(s.App, StringComparer.OrdinalIgnoreCase))
            .Sum(s => (s.End - s.Start).TotalSeconds);

        // per-project + per-class breakdowns come straight from the reclassified slices
        // capuri largi: UI-ul crede listele complete (procente/„altele" calculate din sumă),
        // iar sub-rândurile-variantă consumă sloturi suplimentare — 12/40 trunchiau vizibil
        var projectDetail = projAgg.ToDictionary(
            kv => kv.Key,
            kv => (object)new { apps = TopList(kv.Value.Apps, 100), domains = TopList(kv.Value.Doms, 100) },
            StringComparer.OrdinalIgnoreCase);
        var classDetail = classAgg.ToDictionary(
            kv => kv.Key,
            kv => (object)new { apps = TopList(kv.Value.Apps, 100), domains = TopList(kv.Value.Doms, 100) },
            StringComparer.Ordinal);

        var projectNames = byProject.Keys
            .Concat(claudeWork.Keys)
            .Concat(claudeAttention.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // presence = doar timpul ÎNREGISTRAT (activ + AFK măsurat). Golurile fără niciun
        // eveniment (PC oprit/hibernat, proces mort) NU intră nicăieri — regula userului
        // 2026-07-12: „când calculatorul e oprit, acel timp nu se ia în calcul". AFK =
        // perioadele afk înregistrate între prima și ultima activitate, MINUS intervalele
        // active (regula video marchează timpul audibil activ peste afk-ul brut).
        double presenceSec = 0, afkSec = 0;
        var afkSegs = new List<(DateTimeOffset S, DateTimeOffset E)>();
        if (active.Count > 0)
        {
            var pStart = active.Min(a => a.Start);
            var pEnd = active.Max(a => a.End);
            var afkRaw = afk
                .Where(e => Str(e.Data, "status") == "afk" && e.Duration > 0)
                .Select(e => (S: e.Timestamp < pStart ? pStart : e.Timestamp,
                              E: e.Timestamp.AddSeconds(e.Duration) > pEnd ? pEnd : e.Timestamp.AddSeconds(e.Duration)))
                .Where(iv => iv.E > iv.S)
                .OrderBy(iv => iv.S)
                .ToList();
            afkSegs = SubtractIntervals(afkRaw, active);
            afkSec = afkSegs.Sum(x => (x.E - x.S).TotalSeconds);
            presenceSec = activeSec + afkSec; // ecuația din UI e exactă: activ = prezent − AFK
        }

        const int timelineCap = 2000;
        var timelineRuns = BuildTimelineRuns(slices);
        var timeline = timelineRuns
            .Skip(Math.Max(0, timelineRuns.Count - timelineCap))
            .Select(r => new
            {
                t = r.Start,
                d = Math.Round((r.End - r.Start).TotalSeconds, 1),
                project = r.Project,
                cls = r.Cls,
            });

        return new
        {
            from,
            to,
            totals = new
            {
                activeSeconds = Math.Round(activeSec),
                claudeWorkSeconds = Math.Round(cwork.Sum(e => e.Duration)),
                browserSeconds = Math.Round(browserSec),
                presenceSeconds = Math.Round(presenceSec),
                afkSeconds = Math.Round(afkSec),
                byClass = byClass.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value)),
            },
            afkTimeline = afkSegs.Select(x => new { t = x.S, d = Math.Round((x.E - x.S).TotalSeconds, 1) }),
            classDetail,
            projectDetail,
            byProject = projectNames
                .Select(p => new
                {
                    name = p,
                    seconds = Math.Round(byProject.GetValueOrDefault(p)),
                    claudeWorkSeconds = Math.Round(claudeWork.GetValueOrDefault(p)),
                    claudeAttentionSeconds = Math.Round(claudeAttention.GetValueOrDefault(p)),
                })
                .OrderByDescending(x => x.seconds + x.claudeWorkSeconds)
                .ToList(),
            byApp = TopList(byApp),
            byDomain = TopList(byDomain),
            byProfile = TopList(byProfile),
            focus = focusInfo,
            heatmap = BuildHeatmapSlices(slices, from),
            timeline,
            timelineTruncated = timelineRuns.Count > timelineCap,
        };
    }

    /// <summary>Clip events to [from, to]: boundary-crossing events contribute only their
    /// in-range portion, so adjacent day queries PARTITION time (no double/under-counting).</summary>
    private static List<AwEvent> ClipToRange(List<AwEvent> events, DateTimeOffset from, DateTimeOffset to)
    {
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var end = e.Timestamp.AddSeconds(e.Duration);
            var s = e.Timestamp < from ? from : e.Timestamp;
            var t = end > to ? to : end;
            if (s == e.Timestamp && t == end) continue;
            events[i] = e with { Timestamp = s, Duration = Math.Max(0, (t - s).TotalSeconds) };
        }
        return events;
    }

    // ---- retroactive classification core (TimeCamp model) ---------------------

    /// <summary>Variant = eticheta atribuirii pe INTERVAL („ClientX", „neproductiv") — listele
    /// Aplicații/Domenii afișează felia custom ca rând separat („zoom.exe → ClientX"), iar
    /// rândul simplu rămâne diferența pe standard. Gol = felie neatinsă de intervale.</summary>
    private sealed record ClassifiedSlice(
        DateTimeOffset Start, DateTimeOffset End, string App, string? Domain, string Cls, string Project,
        string Variant = "");

    private sealed record TimelineRun(DateTimeOffset Start, DateTimeOffset End, string Cls, string Project);

    /// <summary>Interval subtraction (a − b); b must be sorted ascending and non-overlapping.</summary>
    private static List<(DateTimeOffset S, DateTimeOffset E)> SubtractIntervals(
        List<(DateTimeOffset S, DateTimeOffset E)> from,
        List<(DateTimeOffset Start, DateTimeOffset End)> minus)
    {
        var result = new List<(DateTimeOffset, DateTimeOffset)>();
        foreach (var iv in from)
        {
            var cur = iv.S;
            foreach (var m in minus)
            {
                if (m.End <= cur) continue;
                if (m.Start >= iv.E) break;
                if (m.Start > cur) result.Add((cur, m.Start));
                if (m.End > cur) cur = m.End;
                if (cur >= iv.E) break;
            }
            if (cur < iv.E) result.Add((cur, iv.E));
        }
        return result;
    }

    /// <summary>Window events ∩ active intervals, re-classified and re-attributed with CURRENT rules.
    /// URL/channel/profile are joined from the web bucket by slice midpoint.</summary>
    private List<ClassifiedSlice> BuildSlices(
        List<AwEvent> windowEvents, List<AwEvent> webEvents,
        List<(DateTimeOffset Start, DateTimeOffset End)> active)
    {
        var cfg = _config.Current;
        var classifier = new ClassificationEngine();
        windowEvents = BridgeWindowGaps(windowEvents);
        // keep zero-duration events: interleaved multi-profile history never merged, and
        // those events are still valid point samples of "which tab/profile was in front"
        var web = webEvents.OrderBy(e => e.Timestamp).ToList();
        var aumidProfile = LearnAumidProfiles(cfg, windowEvents, web);
        var slices = new List<ClassifiedSlice>();
        var wi = 0;
        foreach (var (e, s, t) in IntersectSlices(windowEvents, active))
        {
            var app = Str(e.Data, "app") ?? "unknown";
            var title = Str(e.Data, "title") ?? "";
            var aumid = Str(e.Data, "aumid") ?? "";
            string? url = null, channel = null, profile = null;
            var mid = s + (t - s) / 2;
            // keep events that ended within the tolerance window before mid (point samples)
            var webTol = TimeSpan.FromSeconds(cfg.Browser.PulsetimeSeconds);
            while (wi < web.Count && web[wi].Timestamp.AddSeconds(web[wi].Duration) <= mid - webTol) wi++;
            var isBrowserSlice = cfg.Browser.Processes.Contains(app, StringComparer.OrdinalIgnoreCase);
            // browser context belongs ONLY to browser windows (stale web heartbeats must
            // not attach a domain to e.g. WhatsApp slices)
            if (isBrowserSlice)
            {
                // Among web events near the midpoint (several browser profiles may report),
                // prefer the tab whose title matches the window title — deterministic pick.
                // Zero-duration events are treated as POINT SAMPLES within a tolerance window:
                // interleaved multi-profile history never merged, so it has duration 0.
                var tol = TimeSpan.FromSeconds(cfg.Browser.PulsetimeSeconds);
                AwEvent? chosen = null;
                var chosenTitled = false;
                var chosenDist = TimeSpan.MaxValue;
                for (var j = wi; j < web.Count && web[j].Timestamp <= mid + tol; j++)
                {
                    var wStart = web[j].Timestamp;
                    var wEnd = wStart.AddSeconds(web[j].Duration);
                    var covers = wStart <= mid && wEnd > mid;
                    var dist = covers
                        ? TimeSpan.Zero
                        : (mid < wStart ? wStart - mid : mid - wEnd);
                    if (!covers && dist > tol) continue;

                    var wTitle = Str(web[j].Data, "title") ?? "";
                    var titled = wTitle.Length >= 3 && title.Contains(wTitle, StringComparison.OrdinalIgnoreCase);
                    // a title match always wins; otherwise the closest sample in time wins
                    var better = chosen is null
                        || (titled && !chosenTitled)
                        || (titled == chosenTitled && dist < chosenDist);
                    if (!better) continue;
                    chosen = web[j];
                    chosenTitled = titled;
                    chosenDist = dist;
                }
                // Only a TITLE-matched sample proves which profile owned this window; a mere
                // nearest sample may come from another browser/profile that wrongly claimed
                // focus. Otherwise fall back to the AUMID→profile map (native per-profile id).
                if (chosen is not null && chosenTitled)
                {
                    url = Str(chosen.Data, "url");
                    channel = Str(chosen.Data, "channel");
                    profile = Str(chosen.Data, "profile");
                }
                else if (aumid.Length > 0 && aumidProfile.TryGetValue(aumid, out var learned))
                {
                    profile = learned;
                }
            }
            var cls = classifier.Classify(cfg, app, title, url, channel).Class;
            var project = ResolveProject(cfg, app, title, aumid, url, profile) ?? "";
            string? domain = null;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0)
                domain = u.Host;
            // atribuiri punctuale pe zi („Zoom-ul de AZI → clientul X", „WhatsApp-ul de AZI
            // e productiv") — bat orice regulă, dar numai în ziua respectivă (ora locală).
            // Precedență: INTERVALUL („14:00-15:30") bate ziua întreagă; în același nivel,
            // DOMENIUL e mai specific decât APLICAȚIA (determinism 2026-07-12 — altfel
            // „chrome → X" ar fura feliile site-urilor atribuite explicit altcuiva).
            // O felie care traversează granițele unui interval se TAIE la granițe, ca
            // fiecare bucată să meargă la proiectul/clasa corectă.
            AssignmentConfig? dayHit = null;
            List<(DateTimeOffset From, DateTimeOffset To, AssignmentConfig A)>? spans = null;
            if (cfg.Assignments.Count > 0)
            {
                var localDay = s.ToLocalTime();
                var dayKey = localDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                foreach (var pass in new[] { "domain", "app" })
                {
                    foreach (var a in cfg.Assignments)
                    {
                        if (a.Date != dayKey || a.Match != pass) continue;
                        var matches = pass == "app"
                            ? string.Equals(app, a.Value, StringComparison.OrdinalIgnoreCase)
                            // fallback-ul pe titlu doar pentru ferestre de BROWSER: altfel
                            // „domain:zoom" ar fura felia aplicației desktop Zoom.exe
                            : ClassificationEngine.MatchesDomain(a.Value, url, title, titleFallback: isBrowserSlice);
                        if (!matches) continue;
                        if (a.HasInterval)
                        {
                            var from = LocalInstant(localDay.Date, a.From);
                            var toI = LocalInstant(localDay.Date, a.To);
                            if (toI > s && from < t)
                                (spans ??= new()).Add((from, toI, a)); // ordinea = precedența (domain înaintea app)
                        }
                        else
                        {
                            dayHit ??= a; // primul per precedență; NU break — intervalele se strâng în continuare
                        }
                    }
                }
            }
            // straturi: atribuirea pe ZI se aplică întâi (peste reguli), apoi INTERVALUL
            // deasupra ei, doar pe câmpurile pe care le definește — „13:00-13:30 productiv"
            // nu anulează „tot Zoom-ul de azi → Paliță" în acea jumătate de oră
            if (dayHit is not null)
            {
                if (dayHit.Project.Length > 0) project = dayHit.Project;
                if (dayHit.Class.Length > 0) cls = dayHit.Class;
            }
            if (spans is null)
            {
                slices.Add(new ClassifiedSlice(s, t, app, domain, cls, project));
            }
            else
            {
                var cuts = new SortedSet<DateTimeOffset> { s, t };
                foreach (var (from, toI, _) in spans)
                {
                    if (from > s && from < t) cuts.Add(from);
                    if (toI > s && toI < t) cuts.Add(toI);
                }
                var pts = cuts.ToList();
                for (var k = 0; k + 1 < pts.Count; k++)
                {
                    var ss = pts[k];
                    var tt = pts[k + 1];
                    var m = ss + (tt - ss) / 2;
                    var a = spans.FirstOrDefault(h => h.From <= m && m < h.To).A;
                    var p2 = project;
                    var c2 = cls;
                    var variant = "";
                    if (a is not null)
                    {
                        if (a.Project.Length > 0) p2 = a.Project;
                        if (a.Class.Length > 0) c2 = a.Class;
                        variant = string.Join(" · ", new[]
                        {
                            a.Project,
                            a.Class switch { "productive" => "productiv", "neutral" => "neutru", "unproductive" => "neproductiv", _ => "" },
                        }.Where(x => x.Length > 0));
                    }
                    slices.Add(new ClassifiedSlice(ss, tt, app, domain, c2, p2, variant));
                }
            }
        }
        return slices;
    }

    /// <summary>Local midnight of the given calendar date, with THAT date's UTC offset (DST-safe).</summary>
    public static DateTimeOffset LocalMidnight(DateTime date) =>
        new(date.Date, TimeZoneInfo.Local.GetUtcOffset(date.Date));

    /// <summary>Start of the NEXT local calendar day after t (DST-safe).</summary>
    private static DateTimeOffset NextLocalMidnight(DateTimeOffset t) =>
        LocalMidnight(t.ToLocalTime().Date.AddDays(1));

    /// <summary>Local "HH:mm[:ss]" on a local calendar day → absolute instant (DST-safe via local offset).</summary>
    internal static DateTimeOffset LocalInstant(DateTime localDate, string hhmm)
    {
        var time = TimeOnly.ParseExact(hhmm, new[] { "HH:mm", "HH:mm:ss" }, System.Globalization.CultureInfo.InvariantCulture);
        return new DateTimeOffset(DateTime.SpecifyKind(localDate.Date + time.ToTimeSpan(), DateTimeKind.Unspecified),
            TimeZoneInfo.Local.GetUtcOffset(localDate.Date + time.ToTimeSpan()));
    }

    /// <summary>
    /// Segmentele în care ținta (app sau domeniu) a rulat EFECTIV în intervalul cerut —
    /// baza alocării „pe minute": serverul convertește minutele cerute în ferestre orare
    /// reale peste aceste segmente, deci suma mutată e exactă, nu o fereastră de ceas.
    /// </summary>
    public async Task<List<(DateTimeOffset S, DateTimeOffset E)>> TargetUsageAsync(
        DateTimeOffset from, DateTimeOffset to, string match, string value, CancellationToken ct)
    {
        // clip: with overlap fetch semantics a boundary-crossing event could otherwise
        // yield segments OUTSIDE [from, to] — allocated minutes must stay in the window
        var proj = ClipToRange(await _aw.GetEventsRangeAsync(AwBuckets.Project(_host), from, to, ct: ct), from, to);
        var win = ClipToRange(await _aw.GetEventsRangeAsync(AwBuckets.Window(_host), from, to, ct: ct), from, to);
        var web = ClipToRange(await _aw.GetEventsRangeAsync(AwBuckets.Web(_host), from, to, ct: ct), from, to);
        var active = MergeIntervals(proj);
        var slices = BuildSlices(win, web, active);
        bool Matches(ClassifiedSlice s) => match == "app"
            ? s.App.Equals(value, StringComparison.OrdinalIgnoreCase)
            : s.Domain is not null
              && (s.Domain.Equals(value, StringComparison.OrdinalIgnoreCase)
                  || s.Domain.EndsWith("." + value, StringComparison.OrdinalIgnoreCase));
        var segs = new List<(DateTimeOffset S, DateTimeOffset E)>();
        foreach (var s in slices.Where(Matches).OrderBy(s => s.Start))
        {
            if (segs.Count > 0 && s.Start <= segs[^1].E)
                segs[^1] = (segs[^1].S, s.End > segs[^1].E ? s.End : segs[^1].E);
            else
                segs.Add((s.Start, s.End));
        }
        return segs;
    }

    /// <summary>
    /// Learns AppUserModelID → extension profile label from windows whose title contains the
    /// reported tab title (a 1:1 proof). Chrome/Edge give each profile a distinct AUMID, so this
    /// keeps profile attribution working for windows with no usable web heartbeat — and prevents
    /// a profile that wrongly claimed focus from stealing another browser's time.
    /// </summary>
    private static Dictionary<string, string> LearnAumidProfiles(
        TrackerConfig cfg, List<AwEvent> windowEvents, List<AwEvent> web)
    {
        var tol = TimeSpan.FromSeconds(cfg.Browser.PulsetimeSeconds);
        var votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var w in windowEvents)
        {
            var app = Str(w.Data, "app") ?? "";
            if (!cfg.Browser.Processes.Contains(app, StringComparer.OrdinalIgnoreCase)) continue;
            var aumid = Str(w.Data, "aumid") ?? "";
            var title = Str(w.Data, "title") ?? "";
            if (aumid.Length == 0 || title.Length == 0) continue;

            var mid = w.Timestamp.AddSeconds(w.Duration / 2);
            foreach (var e in web)
            {
                if (e.Timestamp > mid + tol) break;
                if (e.Timestamp.AddSeconds(e.Duration) < mid - tol) continue;
                var tabTitle = Str(e.Data, "title") ?? "";
                var prof = Str(e.Data, "profile");
                if (string.IsNullOrEmpty(prof) || tabTitle.Length < 3) continue;
                if (!title.Contains(tabTitle, StringComparison.OrdinalIgnoreCase)) continue;

                if (!votes.TryGetValue(aumid, out var inner))
                {
                    inner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    votes[aumid] = inner;
                }
                inner[prof] = inner.GetValueOrDefault(prof) + 1;
            }
        }
        return votes.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderByDescending(x => x.Value).First().Key,
            StringComparer.Ordinal);
    }

    /// <summary>Same precedence as the live engine, minus the hold: pins > profile > keywords.</summary>
    private static string? ResolveProject(TrackerConfig cfg, string app, string title, string aumid, string? url, string? profile)
    {
        foreach (var p in cfg.Projects)
        {
            if (p.Apps.Any(a => a.Equals(app, StringComparison.OrdinalIgnoreCase))) return p.Name;
            if (p.Domains.Any(d => d.Length > 0 && ClassificationEngine.MatchesDomain(d, url, title))) return p.Name;
        }
        if (cfg.Browser.Processes.Contains(app, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var p in cfg.Projects)
            {
                foreach (var f in p.BrowserProfiles)
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    if (profile?.Contains(f, StringComparison.OrdinalIgnoreCase) == true) return p.Name;
                    if (aumid.Contains(f, StringComparison.OrdinalIgnoreCase)) return p.Name;
                    if (title.Contains(f, StringComparison.OrdinalIgnoreCase)) return p.Name;
                }
            }
        }
        var hay = $"{app} {title} {url}";
        string? best = null;
        var bestLen = 0;
        foreach (var p in cfg.Projects)
        {
            foreach (var kw in p.Keywords)
            {
                if (kw.Length > bestLen && AttributionEngine.ContainsWholeWord(hay, kw))
                {
                    best = p.Name;
                    bestLen = kw.Length;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Bridges sub-3s gaps between consecutive window events: every window/tab switch loses
    /// ~1s in the bucket (event N ends at its last heartbeat, N+1 starts at its first) —
    /// measured 2026-07-11 at ~48 min/day across ~2,000 crumbs. The gap belongs to the
    /// PRECEDING window (it was still on screen). Report-side only; raw data untouched.
    /// </summary>
    public static List<AwEvent> BridgeWindowGaps(List<AwEvent> events, double maxGapSeconds = 3)
    {
        var asc = events.OrderBy(e => e.Timestamp).ToList();
        for (var i = 0; i < asc.Count - 1; i++)
        {
            var end = asc[i].Timestamp.AddSeconds(asc[i].Duration);
            var gap = (asc[i + 1].Timestamp - end).TotalSeconds;
            if (gap > 0 && gap <= maxGapSeconds)
                asc[i] = asc[i] with { Duration = asc[i].Duration + gap };
        }
        return asc;
    }

    private static IEnumerable<(AwEvent Event, DateTimeOffset Start, DateTimeOffset End)> IntersectSlices(
        List<AwEvent> events, List<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var sorted = events.Where(e => e.Duration > 0).OrderBy(e => e.Timestamp).ToList();
        var i = 0;
        foreach (var e in sorted)
        {
            var start = e.Timestamp;
            var end = e.Timestamp.AddSeconds(e.Duration);
            while (i < intervals.Count && intervals[i].End <= start) i++;
            for (var j = i; j < intervals.Count && intervals[j].Start < end; j++)
            {
                var s = intervals[j].Start > start ? intervals[j].Start : start;
                var t = intervals[j].End < end ? intervals[j].End : end;
                if (t > s) yield return (e, s, t);
            }
        }
    }

    private static List<TimelineRun> BuildTimelineRuns(List<ClassifiedSlice> slices)
    {
        var runs = new List<TimelineRun>();
        foreach (var s in slices.OrderBy(x => x.Start))
        {
            if (runs.Count > 0)
            {
                var last = runs[^1];
                if (s.Cls == last.Cls && s.Project == last.Project && (s.Start - last.End).TotalSeconds <= 3)
                {
                    runs[^1] = last with { End = s.End > last.End ? s.End : last.End };
                    continue;
                }
            }
            runs.Add(new TimelineRun(s.Start, s.End, s.Cls, s.Project));
        }
        return runs;
    }

    /// <summary>Heatmap rows from reclassified slices (per local date × hour).</summary>
    private static List<object> BuildHeatmapSlices(List<ClassifiedSlice> slices, DateTimeOffset from)
    {
        // conversie locală PER-INSTANT (nu offset-ul ancorei de interval): un offset fix
        // deplasează toate orele cu ±1h în zilele de schimbare DST
        var days = new SortedDictionary<string, (double[] A, double[] P, double[] U)>(StringComparer.Ordinal);
        foreach (var sl in slices)
        {
            var start = sl.Start.ToLocalTime();
            var end = sl.End.ToLocalTime();
            var cur = start;
            while (cur < end)
            {
                cur = cur.ToLocalTime(); // re-rezolvă offset-ul după o trecere de graniță DST
                var hourEnd = new DateTimeOffset(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0, cur.Offset).AddHours(1);
                var sliceEnd = hourEnd < end ? hourEnd : end;
                var sec = (sliceEnd - cur).TotalSeconds;
                var key = cur.ToString("yyyy-MM-dd");
                if (!days.TryGetValue(key, out var row))
                {
                    row = (new double[24], new double[24], new double[24]);
                    days[key] = row;
                }
                row.A[cur.Hour] += sec;
                if (sl.Cls == "productive") row.P[cur.Hour] += sec;
                else if (sl.Cls == "unproductive") row.U[cur.Hour] += sec;
                cur = sliceEnd;
            }
        }
        return days.Select(kv => (object)new
        {
            date = kv.Key,
            active = kv.Value.A.Select(v => Math.Round(v)).ToArray(),
            productive = kv.Value.P.Select(v => Math.Round(v)).ToArray(),
            unproductive = kv.Value.U.Select(v => Math.Round(v)).ToArray(),
        }).ToList();
    }

    /// <summary>Rows per local date × 24 hour cells; event durations split on hour boundaries.</summary>
    private static List<object> BuildHeatmap(List<AwEvent> events, DateTimeOffset from)
    {
        var days = new SortedDictionary<string, (double[] A, double[] P, double[] U)>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.Duration <= 0) continue;
            var cls = Str(e.Data, "class") ?? "neutral";
            var start = e.Timestamp.ToLocalTime();
            var end = start.AddSeconds(e.Duration);
            var cur = start;
            while (cur < end)
            {
                cur = cur.ToLocalTime();
                var hourEnd = new DateTimeOffset(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0, cur.Offset).AddHours(1);
                var sliceEnd = hourEnd < end ? hourEnd : end;
                var sec = (sliceEnd - cur).TotalSeconds;
                var key = cur.ToString("yyyy-MM-dd");
                if (!days.TryGetValue(key, out var row))
                {
                    row = (new double[24], new double[24], new double[24]);
                    days[key] = row;
                }
                row.A[cur.Hour] += sec;
                if (cls == "productive") row.P[cur.Hour] += sec;
                else if (cls == "unproductive") row.U[cur.Hour] += sec;
                cur = sliceEnd;
            }
        }
        return days.Select(kv => (object)new
        {
            date = kv.Key,
            active = kv.Value.A.Select(v => Math.Round(v)).ToArray(),
            productive = kv.Value.P.Select(v => Math.Round(v)).ToArray(),
            unproductive = kv.Value.U.Select(v => Math.Round(v)).ToArray(),
        }).ToList();
    }

    /// <summary>
    /// Focus Score = 0.7 × ClassScore + 0.3 × FlowScore. ClassScore = share of productive
    /// (+half credit for neutral) in active time; FlowScore penalizes context switching:
    /// ≤30 app switches per active hour are free, 150/h zeroes it. Under 5 min active → no score.
    /// </summary>
    private static object ComputeFocus(
        List<AwEvent> windowEvents,
        List<(DateTimeOffset Start, DateTimeOffset End)> active,
        Dictionary<string, double> byClass,
        double activeSec)
    {
        var switches = 0;
        string? prevApp = null;
        foreach (var (e, _) in Intersect(windowEvents, active))
        {
            var app = Str(e.Data, "app") ?? "";
            if (prevApp is not null && !app.Equals(prevApp, StringComparison.OrdinalIgnoreCase)) switches++;
            prevApp = app;
        }
        var activeHours = activeSec / 3600.0;
        var sph = activeHours > 0.05 ? switches / activeHours : 0;
        var classScore = activeSec > 0
            ? 100.0 * (byClass.GetValueOrDefault("productive") + 0.5 * byClass.GetValueOrDefault("neutral")) / activeSec
            : 0;
        var flowScore = 100.0 * Math.Clamp(1.0 - Math.Max(0, sph - 30.0) / 120.0, 0, 1);
        int? score = activeSec < 300 ? null : (int)Math.Round(0.7 * classScore + 0.3 * flowScore);
        return new
        {
            score,
            classScore = Math.Round(classScore),
            flowScore = Math.Round(flowScore),
            switches,
            switchesPerHour = Math.Round(sph, 1),
        };
    }

    // ---- F2: weekly report + streaks -----------------------------------------

    public async Task<object> BuildWeeklyAsync(DateTimeOffset anchor, int streakThresholdMinutes, CancellationToken ct)
    {
        var local = anchor.ToLocalTime();
        var monday = LocalMidnight(local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)));
        var curTask = WeekSummaryAsync(monday, ct);
        var prevTask = WeekSummaryAsync(LocalMidnight(monday.Date.AddDays(-7)), ct);
        var streakTask = StreakAsync(streakThresholdMinutes, ct);
        await Task.WhenAll(curTask, prevTask, streakTask);
        return new { current = curTask.Result, previous = prevTask.Result, streak = streakTask.Result };
    }

    private async Task<object> WeekSummaryAsync(DateTimeOffset monday, CancellationToken ct)
    {
        var to = LocalMidnight(monday.Date.AddDays(7));
        var projTask = _aw.GetEventsRangeAsync(AwBuckets.Project(_host), monday, to, ct: ct);
        var winTask = _aw.GetEventsRangeAsync(AwBuckets.Window(_host), monday, to, ct: ct);
        var webTask = _aw.GetEventsRangeAsync(AwBuckets.Web(_host), monday, to, ct: ct);
        await Task.WhenAll(projTask, winTask, webTask);
        // clip to the week so boundary-crossing events don't leak across weeks (see BuildAsync)
        var projEvents = ClipToRange(projTask.Result, monday, to);
        var winEvents = ClipToRange(winTask.Result, monday, to);
        var webEvents = ClipToRange(webTask.Result, monday, to);
        var active = MergeIntervals(projEvents);
        // RETROACTIVE: same reclassified slices as the dashboard report
        var slices = BuildSlices(winEvents, webEvents, active);

        var byClass = new Dictionary<string, double>();
        var byProject = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var byApp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var days = new (double P, double N, double U)[7];
        foreach (var s in slices)
        {
            var sec = (s.End - s.Start).TotalSeconds;
            byClass[s.Cls] = byClass.GetValueOrDefault(s.Cls) + sec;
            byApp[s.App] = byApp.GetValueOrDefault(s.App) + sec;
            if (s.Project.Length > 0) byProject[s.Project] = byProject.GetValueOrDefault(s.Project) + sec;

            var start = s.Start.ToLocalTime();
            var end = s.End.ToLocalTime();
            var cur = start;
            while (cur < end)
            {
                var dayEnd = NextLocalMidnight(cur);
                var sliceEnd = dayEnd < end ? dayEnd : end;
                var idx = (cur.ToLocalTime().Date - monday.Date).Days;
                if (idx is >= 0 and < 7)
                {
                    var d = (sliceEnd - cur).TotalSeconds;
                    if (s.Cls == "productive") days[idx].P += d;
                    else if (s.Cls == "unproductive") days[idx].U += d;
                    else days[idx].N += d;
                }
                cur = sliceEnd;
            }
        }
        var activeSec = slices.Sum(s => (s.End - s.Start).TotalSeconds);

        return new
        {
            from = monday,
            to,
            activeSeconds = Math.Round(activeSec),
            byClass = byClass.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value)),
            focus = ComputeFocus(winEvents, active, byClass, activeSec),
            days = Enumerable.Range(0, 7).Select(i => new
            {
                date = monday.AddDays(i).ToString("yyyy-MM-dd"),
                productive = Math.Round(days[i].P),
                neutral = Math.Round(days[i].N),
                unproductive = Math.Round(days[i].U),
            }).ToList(),
            topProjects = TopList(byProject, 5),
            topApps = TopList(byApp, 5),
        };
    }

    /// <summary>Streak = consecutive days with ≥ threshold minutes of PRODUCTIVE time (90-day window).</summary>
    private async Task<object> StreakAsync(int thresholdMinutes, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var today = LocalMidnight(now.Date);
        var windowStart = today.AddDays(-90);
        var proj = await _aw.GetEventsRangeAsync(AwBuckets.Project(_host), windowStart, today.AddDays(1), ct: ct);

        var perDay = new Dictionary<string, double>(StringComparer.Ordinal);
        void AddSplit(DateTimeOffset s0, DateTimeOffset e0)
        {
            var cur = s0.ToLocalTime();
            var end = e0.ToLocalTime();
            while (cur < end)
            {
                var dayEnd = NextLocalMidnight(cur);
                var sliceEnd = dayEnd < end ? dayEnd : end;
                var key = cur.ToLocalTime().ToString("yyyy-MM-dd");
                perDay[key] = perDay.GetValueOrDefault(key) + (sliceEnd - cur).TotalSeconds;
                cur = sliceEnd;
            }
        }
        foreach (var e in proj)
        {
            if (e.Duration <= 0) continue;
            if ((Str(e.Data, "class") ?? "") != "productive") continue;
            AddSplit(e.Timestamp, e.Timestamp.AddSeconds(e.Duration));
        }

        // clasa din bucket e ÎNGHEȚATĂ la momentul capturii — o reclasificare ulterioară
        // nu s-ar vedea în streak. Fereastra afișată (ultimele 14 zile, care acoperă și
        // streak-ul curent tipic) se recalculează RETROACTIV din feliile reclasificate;
        // restul ferestrei de 90 de zile rămâne pe clasa înghețată (cost/beneficiu).
        var overlayFrom = LocalMidnight(today.Date.AddDays(-13));
        var overlayTo = LocalMidnight(today.Date.AddDays(1));
        var winTask = _aw.GetEventsRangeAsync(AwBuckets.Window(_host), overlayFrom, overlayTo, ct: ct);
        var webTask = _aw.GetEventsRangeAsync(AwBuckets.Web(_host), overlayFrom, overlayTo, ct: ct);
        await Task.WhenAll(winTask, webTask);
        var projOverlay = ClipToRange(
            proj.Where(e => e.Timestamp.AddSeconds(e.Duration) > overlayFrom).ToList(), overlayFrom, overlayTo);
        var slicesOverlay = BuildSlices(
            ClipToRange(winTask.Result, overlayFrom, overlayTo),
            ClipToRange(webTask.Result, overlayFrom, overlayTo),
            MergeIntervals(projOverlay));
        for (var i = 0; i < 14; i++)
            perDay.Remove(today.AddDays(-13 + i).ToString("yyyy-MM-dd"));
        foreach (var sl in slicesOverlay)
        {
            if (sl.Cls != "productive") continue;
            AddSplit(sl.Start, sl.End);
        }

        var thresholdSec = thresholdMinutes * 60.0;
        bool Met(DateTimeOffset d) => perDay.GetValueOrDefault(d.ToString("yyyy-MM-dd")) >= thresholdSec;

        var current = 0;
        var cursor = Met(today) ? today : today.AddDays(-1); // today is in progress — an unmet today doesn't break it
        while (Met(cursor))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }

        var best = 0;
        var run = 0;
        for (var d = windowStart; d <= today; d = d.AddDays(1))
        {
            if (Met(d))
            {
                run++;
                if (run > best) best = run;
            }
            else
            {
                run = 0;
            }
        }

        var last14 = Enumerable.Range(0, 14)
            .Select(i => today.AddDays(-13 + i))
            .Select(d => new
            {
                date = d.ToString("yyyy-MM-dd"),
                productiveSeconds = Math.Round(perDay.GetValueOrDefault(d.ToString("yyyy-MM-dd"))),
                met = Met(d),
            })
            .ToList();

        return new { current, best, thresholdMinutes, last14 };
    }

    // ---- F3: daily journal --------------------------------------------------

    public async Task<object> BuildJournalAsync(DateTimeOffset day, CancellationToken ct)
    {
        var local = day.ToLocalTime();
        var from = new DateTimeOffset(local.Date, local.Offset);
        var to = LocalMidnight(local.Date.AddDays(1)); // DST-safe: nu from+24h
        var projTask = _aw.GetEventsRangeAsync(AwBuckets.Project(_host), from, to, ct: ct);
        var winTask = _aw.GetEventsRangeAsync(AwBuckets.Window(_host), from, to, ct: ct);
        var webTask = _aw.GetEventsRangeAsync(AwBuckets.Web(_host), from, to, ct: ct);
        var cworkTask = _aw.GetEventsRangeAsync(AwBuckets.ClaudeWork(_host), from, to, ct: ct);
        await Task.WhenAll(projTask, winTask, webTask, cworkTask);

        // clip to the day so a span crossing midnight counts only its in-day share
        var projEvents = ClipToRange(projTask.Result, from, to);
        var winEvents = ClipToRange(winTask.Result, from, to);
        var webEvents = ClipToRange(webTask.Result, from, to);
        var cwork = ClipToRange(cworkTask.Result, from, to);
        var proj = projEvents.Where(e => e.Duration > 0).OrderBy(e => e.Timestamp).ToList();
        var active = MergeIntervals(proj);
        // RETROACTIVE: same reclassified slices as the dashboard report
        var slices = BuildSlices(winEvents, webEvents, active);

        var byClass = new Dictionary<string, double>();
        var byProject = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var byApp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var byDomain = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in slices)
        {
            var sec = (s.End - s.Start).TotalSeconds;
            byClass[s.Cls] = byClass.GetValueOrDefault(s.Cls) + sec;
            byApp[s.App] = byApp.GetValueOrDefault(s.App) + sec;
            if (s.Domain is not null) byDomain[s.Domain] = byDomain.GetValueOrDefault(s.Domain) + sec;
            if (s.Project.Length > 0) byProject[s.Project] = byProject.GetValueOrDefault(s.Project) + sec;
        }
        var activeSec = slices.Sum(s => (s.End - s.Start).TotalSeconds);

        // longest continuous PRODUCTIVE stretch (gaps under 5 min don't break it)
        DateTimeOffset? runStart = null, runEnd = null, bestStart = null;
        double bestSec = 0;
        var runProjects = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, double> bestProjects = new(StringComparer.OrdinalIgnoreCase);
        foreach (var s in slices)
        {
            if (s.Cls != "productive") continue;
            if (runEnd is not null && (s.Start - runEnd.Value).TotalSeconds <= 300)
            {
                runEnd = s.End > runEnd ? s.End : runEnd;
            }
            else
            {
                runStart = s.Start;
                runEnd = s.End;
                runProjects = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }
            if (s.Project.Length > 0)
                runProjects[s.Project] = runProjects.GetValueOrDefault(s.Project) + (s.End - s.Start).TotalSeconds;
            var runSec = (runEnd.Value - runStart!.Value).TotalSeconds;
            if (runSec > bestSec)
            {
                bestSec = runSec;
                bestStart = runStart;
                bestProjects = runProjects;
            }
        }

        // main distraction: top unproductive domain, else top unproductive app
        var unprod = slices.Where(s => s.Cls == "unproductive").ToList();
        var distraction = unprod
            .Where(s => s.Domain is not null)
            .GroupBy(s => s.Domain!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { name = g.Key, seconds = Math.Round(g.Sum(x => (x.End - x.Start).TotalSeconds)) })
            .OrderByDescending(x => x.seconds)
            .FirstOrDefault()
            ?? unprod
                .GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { name = g.Key, seconds = Math.Round(g.Sum(x => (x.End - x.Start).TotalSeconds)) })
                .OrderByDescending(x => x.seconds)
                .FirstOrDefault();

        var claudeByProject = NormalizeClaudeProjects(SumBy(cwork, "project"));
        return new
        {
            date = from.ToString("yyyy-MM-dd"),
            firstActivity = proj.Count > 0 ? proj[0].Timestamp.ToOffset(from.Offset) : (DateTimeOffset?)null,
            lastActivity = proj.Count > 0 ? proj[^1].Timestamp.AddSeconds(proj[^1].Duration).ToOffset(from.Offset) : (DateTimeOffset?)null,
            activeSeconds = Math.Round(activeSec),
            byClass = byClass.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value)),
            focus = ComputeFocus(winEvents, active, byClass, activeSec),
            topProjects = TopList(byProject, 3),
            topApps = TopList(byApp, 3),
            topDomains = TopList(byDomain, 3),
            claudeWorkSeconds = Math.Round(cwork.Sum(e => e.Duration)),
            claudeTopProject = claudeByProject.OrderByDescending(kv => kv.Value)
                .Select(kv => new { name = kv.Key, seconds = Math.Round(kv.Value) }).FirstOrDefault(),
            longestFocus = bestSec > 0
                ? new
                {
                    start = bestStart!.Value.ToOffset(from.Offset),
                    seconds = Math.Round(bestSec),
                    project = bestProjects.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault(),
                }
                : null,
            distraction,
        };
    }

    private static List<object> TopList(Dictionary<string, double> map, int top = 400) =>
        map.OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => (object)new { name = kv.Key, seconds = Math.Round(kv.Value) })
            .ToList();

    /// <summary>Folds HISTORICAL encoded-dir pseudo-projects (e.g. "C--Users-…-time-tracker-…")
    /// recorded before the jsonl-cwd fix into their configured project names.</summary>
    private Dictionary<string, double> NormalizeClaudeProjects(Dictionary<string, double> map)
    {
        var cfg = _config.Current;
        string Resolve(string name)
        {
            if (!name.StartsWith("C--", StringComparison.OrdinalIgnoreCase)) return name;
            foreach (var p in cfg.Projects)
            {
                foreach (var dir in p.ClaudeDirs)
                {
                    var enc = dir.Replace(":", "-").Replace("\\", "-").Replace("/", "-");
                    if (name.StartsWith(enc, StringComparison.OrdinalIgnoreCase)) return p.Name;
                }
            }
            return name;
        }

        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, sec) in map)
        {
            var key = Resolve(name);
            result[key] = result.GetValueOrDefault(key) + sec;
        }

        // second pass: fold leftover encoded names into their friendly twin (hooks used the
        // cwd basename, jsonl used the encoded path — "C--…-doula-lavie" endsWith "-doula-lavie")
        var friendly = result.Keys.Where(k => !k.StartsWith("C--", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var enc in result.Keys.Where(k => k.StartsWith("C--", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var twin = friendly
                .Where(f => enc.EndsWith("-" + f, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Length)
                .FirstOrDefault();
            if (twin is null) continue;
            result[twin] = result.GetValueOrDefault(twin) + result[enc];
            result.Remove(enc);
        }
        return result;
    }

    private static Dictionary<string, double> SumBy(List<AwEvent> events, string key)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            var k = Str(e.Data, key);
            if (string.IsNullOrEmpty(k)) continue;
            map[k] = map.GetValueOrDefault(k) + e.Duration;
        }
        return map;
    }

    private static string? Str(JsonElement data, string prop) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// Merges the tracker-project events into "user was active" intervals, bridging gaps
    /// up to 15s (the project pulsetime): every project/class SWITCH leaves a ~5s seam
    /// (old event ends at its last 5s-cadence heartbeat, the new one starts at the next) —
    /// measured 2026-07-11: 988 seams ≈ 85 min/day, all active→active. Real pauses are
    /// ≥3 min (AFK threshold), so a 15s bridge can never invent inactivity as activity.
    /// </summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> MergeIntervals(
        List<AwEvent> events, double bridgeSeconds = 15)
    {
        var sorted = events
            .Where(e => e.Duration > 0)
            .Select(e => (Start: e.Timestamp, End: e.Timestamp.AddSeconds(e.Duration)))
            .OrderBy(iv => iv.Start)
            .ToList();
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var iv in sorted)
        {
            if (merged.Count > 0 && iv.Start <= merged[^1].End.AddSeconds(bridgeSeconds))
            {
                if (iv.End > merged[^1].End) merged[^1] = (merged[^1].Start, iv.End);
            }
            else
            {
                merged.Add(iv);
            }
        }
        return merged;
    }

    /// <summary>Two-pointer sweep: overlap seconds of each event with the active intervals.</summary>
    private static IEnumerable<(AwEvent Event, double OverlapSeconds)> Intersect(
        List<AwEvent> events, List<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var sorted = events.Where(e => e.Duration > 0).OrderBy(e => e.Timestamp).ToList();
        var i = 0;
        foreach (var e in sorted)
        {
            var start = e.Timestamp;
            var end = e.Timestamp.AddSeconds(e.Duration);
            while (i < intervals.Count && intervals[i].End <= start) i++;
            double overlap = 0;
            for (var j = i; j < intervals.Count && intervals[j].Start < end; j++)
            {
                var s = intervals[j].Start > start ? intervals[j].Start : start;
                var t = intervals[j].End < end ? intervals[j].End : end;
                if (t > s) overlap += (t - s).TotalSeconds;
            }
            if (overlap > 0) yield return (e, overlap);
        }
    }
}
