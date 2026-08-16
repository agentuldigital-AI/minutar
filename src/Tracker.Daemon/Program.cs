using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;
using Tracker.Daemon.Claude;
using Tracker.Daemon.Coach;
using Tracker.Daemon.Engine;
using Tracker.Daemon.Popup;
using Tracker.Daemon.Report;
using Tracker.Daemon.State;
using Tracker.Shared.Aw;
using Tracker.Shared.Config;
using Tracker.Shared.Logging;

// Tracker.Daemon — bridge + rules engine (M2, architecture §1.3).
// Popup (M3), Claude module (M4) and the dashboard/report API (M7) build on this.

Log.Init("daemon");

var cfgPath = ConfigLocator.Resolve(args);
using var config = new ConfigProvider(cfgPath);
var cfg = config.Current;
Log.Info($"Config loaded from {cfgPath} (hot-reload on)");

var host = cfg.Server.ResolveBucketHost();

// ---- own event storage (plan docs/PLAN-2026-07-10-remove-activitywatch.md) ----
// STARTUP-ONLY: db path / port / tee are read once here; config hot-reload deliberately
// does NOT touch them (Kestrel is bound and the DB opened exactly once per process).
string? CliArg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
}
var dbPath = CliArg("--db") ?? cfg.Storage.ResolveDbPath();
var bridgePort = int.TryParse(CliArg("--port"), out var portOverride) ? portOverride : cfg.Server.BridgePort;
using var eventStore = new Tracker.Daemon.Storage.EventStore(dbPath, "tracker-daemon", host);
var teeActive = !string.IsNullOrWhiteSpace(cfg.Storage.TeeAwUrl);
// cutover încheiat (13 iul): importul din peewee rulează DOAR explicit (--import-aw <copie>).
// Calea AUTO de la cutover e dezarmată — ar fi reimportat silențios istoricul aw STALE
// peste un store golit accidental (events.db șters manual / recovery de corupție).
if (CliArg("--import-aw") is not null)
{
    try
    {
        await Tracker.Daemon.Storage.AwImporter.RunIfNeededAsync(
            eventStore, cfg.Server.AwUrl, CliArg("--import-aw"), force: args.Contains("--force"));
    }
    catch (Exception ex)
    {
        Log.Error("aw import failed: " + ex.Message);
        throw; // partial import must not masquerade as a healthy store
    }
}
if (args.Contains("--import-only"))
{
    Log.Info("Import-only mode: done, exiting (cutover script continues).");
    return;
}

var windowStore = new WindowStateStore();
var browserStore = new BrowserStateStore();
// M5 (cutover 2026-07-12): internal readers/writers run on the OWN store; while the shadow
// is on ([storage] tee_aw_url set) writes are also forwarded to aw-server for the parallel
// re-verification — reads are ALWAYS local from here on (docs/CUTOVER-2026-07-12.md)
using var teeForward = teeActive
    ? new AwClient(cfg.Storage.TeeAwUrl, "tracker-daemon", host)
    : null;
Tracker.Shared.Storage.IEventStore store = teeForward is not null
    ? new Tracker.Daemon.Storage.TeeEventStore(eventStore, teeForward)
    : eventStore;
var pause = new Tracker.Daemon.Pause.PauseService();
var engine = new RulesEngineService(config, windowStore, browserStore, store, pause, host);
var popupService = new PopupService();
var days = new DayStateStore();
var focus = new Tracker.Daemon.Focus.FocusService(config);
var popupController = new PopupController(config, engine, popupService, focus, windowStore, days);
var claude = new ClaudeModule(config, windowStore, store, host);

var report = new ReportService(store, config, host);
var coach = new CoachEngine(config, engine, popupService, days, cfg.Server.BridgePort, store);
using var telegram = new Tracker.Daemon.Briefing.TelegramClient();
var briefingState = new Tracker.Daemon.Briefing.BriefingStateStore();
var briefing = new Tracker.Daemon.Briefing.BriefingService(config, report, days, briefingState, telegram);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    // the SPA build lands in wwwroot next to the exe — independent of the cwd
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Logging.ClearProviders();
builder.Services.AddHostedService(_ => engine);
builder.Services.AddHostedService(_ => popupController);
builder.Services.AddHostedService(_ => claude);
builder.Services.AddHostedService(_ => coach);
builder.Services.AddHostedService(_ => briefing);
builder.Services.AddHostedService(_ => new Tracker.Daemon.Storage.BackupService(eventStore));
var app = builder.Build();

// aw-server /api/0 compat shim + parity tee (plan M2/M4)
Tracker.Daemon.Storage.StorageEndpoints.Map(app, eventStore, host, cfg.Storage.TeeAwUrl, pause);

// Coach v0 — day state (intent, top-3 priorities, shutdown review)
app.MapGet("/api/day", (string? date) =>
    Results.Json(days.Load(date ?? DateTimeOffset.Now.ToString("yyyy-MM-dd"))));

app.MapPut("/api/day", (DayState state) =>
{
    if (string.IsNullOrEmpty(state.Date)) state.Date = DateTimeOffset.Now.ToString("yyyy-MM-dd");
    var existing = days.Load(state.Date);
    // ritual flags and nudge history are engine-owned — never reset by the UI
    state.IntentPromptShown |= existing.IntentPromptShown;
    state.ShutdownPromptShown |= existing.ShutdownPromptShown;
    state.BriefingSent |= existing.BriefingSent;
    state.Nudges = existing.Nudges;
    days.Save(state);
    return Results.Ok(new { saved = true });
});

app.MapPost("/coach/test", () =>
{
    popupService.ShowToast("Coach (test)", "Salut! Așa arată un nudge — calm, în colț, fără să-ți fure focusul. Click = dashboard.",
        config.Current.Coach.ToastSeconds, () => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo($"http://localhost:{config.Current.Server.BridgePort}") { UseShellExecute = true }));
    return Results.Ok(new { shown = true });
});

// Briefing: previzualizare + trimitere la cerere, ca să nu fie nevoie de o repornire ca să-l testezi.
// „preview" nu atinge Telegram și nu marchează ziua — doar arată textul.
app.MapPost("/briefing/test", async (bool? preview, string? kind, CancellationToken ct) =>
{
    var text = kind?.ToLowerInvariant() switch
    {
        "week" => await briefing.ComposePeriodAsync(Tracker.Daemon.Briefing.BriefingPeriod.Week, ct),
        "month" => await briefing.ComposePeriodAsync(Tracker.Daemon.Briefing.BriefingPeriod.Month, ct),
        _ => await briefing.ComposeAsync(ct),
    };
    if (preview == true) return Results.Ok(new { sent = false, text });

    var t = config.Current.Telegram;
    if (t.BotToken.Trim().Length == 0 || t.ChatId.Trim().Length == 0)
        return Results.BadRequest(new { sent = false, error = "lipsește telegram.bot_token sau telegram.chat_id", text });

    var ok = await telegram.SendAsync(t.BotToken.Trim(), t.ChatId.Trim(), text, ct);
    return Results.Ok(new { sent = ok, text });
});

if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

// dashboard report API (decision #11): day/week/month ranges, computed server-side
app.MapGet("/api/report", async (string? from, string? to, CancellationToken ct) =>
{
    var now = DateTimeOffset.Now;
    // the SPA sends UTC ("Z") instants — convert to LOCAL so hour bucketing (heatmap)
    // uses Romania's clock, not UTC-shifted hours
    var f = from is not null ? DateTimeOffset.Parse(from).ToLocalTime() : new DateTimeOffset(now.Date, now.Offset);
    var t = to is not null ? DateTimeOffset.Parse(to).ToLocalTime() : ReportService.LocalMidnight(f.Date.AddDays(1));
    return Results.Json(await report.BuildAsync(f, t, ct));
});

// F2 — weekly report: current vs previous week + productive-day streaks
app.MapGet("/api/weekly", async (string? anchor, CancellationToken ct) =>
{
    var a = anchor is not null ? DateTimeOffset.Parse(anchor) : DateTimeOffset.Now;
    return Results.Json(await report.BuildWeeklyAsync(a, config.Current.Goals.StreakProductiveMinutes, ct));
});

// F3 — daily journal ("ce ai lucrat azi"), template rendered client-side
app.MapGet("/api/journal", async (string? date, CancellationToken ct) =>
{
    var d = date is not null ? DateTimeOffset.Parse(date) : DateTimeOffset.Now;
    return Results.Json(await report.BuildJournalAsync(d, ct));
});

// ---- phone screen time (entered from outside) --------------------------------
// iOS has no export: Screen Time data cannot leave the device by any supported route
// (the report extension's sandbox blocks every output channel), so the numbers are read
// off screenshots and posted here. WEEKLY summaries — a week is a single zero-duration
// event, never spread across days, because we know the total but not when it happened.
// Zero duration also guarantees it can never be mistaken for measured PC time.

app.MapPost("/api/phone/usage", async (PhoneUsageDto req, CancellationToken ct) =>
{
    if (!DateOnly.TryParse(req.From, out var from) || !DateOnly.TryParse(req.To, out var to))
        return Results.BadRequest(new { error = "from/to trebuie să fie date valide (yyyy-MM-dd)" });
    if (to <= from)
        return Results.BadRequest(new { error = "to trebuie să fie după from" });
    var span = to.DayNumber - from.DayNumber;
    if (span > 31)
        return Results.BadRequest(new { error = "intervalul e prea lung (max 31 de zile)" });
    // a day has 1440 minutes; anything above the span's worth of minutes is a typo, not data
    if (req.TotalMinutes <= 0 || req.TotalMinutes > span * 1440)
        return Results.BadRequest(new { error = $"totalMinutes trebuie să fie între 1 și {span * 1440}" });

    var apps = (req.Apps ?? new List<PhoneAppUsageDto>())
        .Where(a => !string.IsNullOrWhiteSpace(a.Name) && a.Minutes > 0)
        .Select(a => new { name = a.Name.Trim(), minutes = a.Minutes })
        .OrderByDescending(a => a.minutes)
        .ToList();

    var data = new Dictionary<string, object?>
    {
        ["device"] = string.IsNullOrWhiteSpace(req.Device) ? "phone" : req.Device.Trim(),
        ["from"] = from.ToString("yyyy-MM-dd"),
        ["to"] = to.ToString("yyyy-MM-dd"),
        ["totalMinutes"] = req.TotalMinutes,
        ["avgDailyMinutes"] = (int)Math.Round((double)req.TotalMinutes / span),
        ["apps"] = apps,
        ["pickups"] = req.Pickups,
        ["notifications"] = req.Notifications,
        ["source"] = string.IsNullOrWhiteSpace(req.Source) ? "screen-time-screenshot" : req.Source.Trim(),
        // a re-send of the same week is kept, not merged: the newest write wins on read,
        // so a correction never has to delete anything
        ["recordedAt"] = DateTimeOffset.Now.ToString("o"),
    };

    try
    {
        await store.EnsureBucketAsync(AwBuckets.PhoneUsage(host), AwBuckets.PhoneUsageType, ct);
        await store.HeartbeatAsync(
            AwBuckets.PhoneUsage(host), data,
            pulsetimeSeconds: 0, // never merge — each submission is its own record
            timestamp: new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), DateTimeOffset.Now.Offset),
            durationSeconds: 0, ct: ct);
    }
    catch (Exception ex)
    {
        Log.Error("phone usage save failed: " + ex.Message);
        return Results.Problem("nu am putut salva: " + ex.Message);
    }

    Log.Info($"Phone usage saved: {from}..{to} {req.TotalMinutes}m, {apps.Count} aplicații");

    // Datele de telefon vin dintr-un import manual, deci nu pot fi așteptate la o oră fixă.
    // Momentul importului E declanșatorul: ca să fi ajuns aici, aplicația merge deja.
    // Fire-and-forget — un Telegram lent n-are voie să țină clientul în așteptare, iar
    // metoda nu aruncă niciodată: importul e salvat oricum.
    _ = briefing.OnPhoneImportedAsync(from, to, CancellationToken.None);

    return Results.Ok(new { saved = true, from = data["from"], to = data["to"], minutes = req.TotalMinutes });
});

static string? PhoneStr(JsonElement data, string prop) =>
    data.ValueKind == JsonValueKind.Object && data.TryGetProperty(prop, out var v)
    && v.ValueKind == JsonValueKind.String
        ? v.GetString()
        : null;

app.MapGet("/api/phone/usage", async (string? from, string? to, CancellationToken ct) =>
{
    var f = from is not null ? DateTimeOffset.Parse(from) : DateTimeOffset.Now.AddDays(-120);
    var t = to is not null ? DateTimeOffset.Parse(to) : DateTimeOffset.Now.AddDays(1);
    var events = await store.GetEventsRangeAsync(AwBuckets.PhoneUsage(host), f, t, ct: ct);

    // one entry per (device, week): corrections are appended, so the latest write wins
    var latest = events
        .Select(e => new
        {
            Device = PhoneStr(e.Data, "device") ?? "phone",
            From = PhoneStr(e.Data, "from") ?? "",
            RecordedAt = PhoneStr(e.Data, "recordedAt") ?? "",
            Event = e,
        })
        .GroupBy(x => (x.Device, x.From))
        .Select(g => g.OrderByDescending(x => x.RecordedAt, StringComparer.Ordinal).First().Event)
        .OrderBy(e => e.Timestamp)
        .Select(e => e.Data)
        .ToList();

    return Results.Json(new { weeks = latest });
});

// Aplicațiile văzute în importuri care nu au încă o clasă. Se recalculează la FIECARE
// cerere, nu doar la primul import: o aplicație nouă apărută în a cincea săptămână
// trebuie clasificată la fel ca cele din prima.
app.MapGet("/api/phone/unclassified", async (CancellationToken ct) =>
{
    var events = await store.GetEventsRangeAsync(
        AwBuckets.PhoneUsage(host), DateTimeOffset.Now.AddYears(-2), DateTimeOffset.Now.AddDays(1), ct: ct);

    // aceeași cheie ca la raportare: „MemeBox" acoperă și „MemeBox: Funny Pics & GIFs",
    // altfel aceeași aplicație ar fi cerută la clasificare la fiecare import
    var known = config.Current.PhoneApps
        .Select(p => PhoneUsage.MatchKey(p.Name))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // minutele se cumulează peste toate importurile: cele mai folosite apar primele,
    // ca utilizatorul să înceapă cu ce contează
    var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in events)
    {
        if (e.Data.ValueKind != JsonValueKind.Object) continue;
        if (!e.Data.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array) continue;
        foreach (var a in apps.EnumerateArray())
        {
            var name = PhoneStr(a, "name");
            if (string.IsNullOrWhiteSpace(name) || known.Contains(PhoneUsage.MatchKey(name))) continue;
            var min = a.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
            totals[name] = totals.GetValueOrDefault(name) + min;
        }
    }

    return Results.Json(new
    {
        apps = totals.OrderByDescending(kv => kv.Value).Select(kv => new { name = kv.Key, minutes = kv.Value }),
        projects = config.Current.Projects.Select(p => p.Name).Where(n => n.Length > 0),
    });
});

app.MapPost("/api/phone/classify", (PhoneClassifyDto req) =>
{
    var items = (req.Apps ?? new List<PhoneAppRuleDto>())
        .Where(a => !string.IsNullOrWhiteSpace(a.Name))
        .ToList();
    if (items.Count == 0) return Results.BadRequest(new { error = "nicio aplicație de salvat" });
    foreach (var a in items)
    {
        if (a.Class is not ("productive" or "neutral" or "unproductive"))
            return Results.BadRequest(new { error = $"clasă invalidă pentru „{a.Name}”: {a.Class}" });
        if (a.Kind is not (null or "" or "app" or "site" or "browser"))
            return Results.BadRequest(new { error = $"tip invalid pentru „{a.Name}”: {a.Kind}" });
    }

    // ADITIV, ca /api/projects: nu cere token de versiune și nu poate șterge munca altcuiva
    // — încarcă proaspăt de pe disc, adaugă/înlocuiește doar aplicațiile trimise, scrie.
    lock (ConfigWriter.SyncRoot)
    {
        TrackerConfig fresh;
        try
        {
            fresh = TrackerConfig.Load(config.ConfigPath);
        }
        catch (Exception ex)
        {
            Log.Error("phone classify: config load failed — " + ex.Message);
            return Results.Problem("nu am putut citi configul: " + ex.Message);
        }

        foreach (var a in items)
        {
            var name = a.Name.Trim();
            // ACTUALIZARE PARȚIALĂ: un câmp lipsă înseamnă „lasă-l cum e", nu „golește-l".
            // Altfel orice buton care nu trimite tot răspunsul șterge restul — exact ce s-a
            // întâmplat cu tipul: apăsai o clasă și aplicația sărea înapoi din Site-uri în
            // Aplicații, fiindcă butonul de clasă nu avea de unde ști ce tip era.
            var old = fresh.PhoneApps.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            fresh.PhoneApps.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            fresh.PhoneApps.Add(new PhoneAppConfig
            {
                Name = name,
                Class = a.Class,
                Project = a.Project?.Trim() ?? old?.Project ?? "",
                Kind = string.IsNullOrWhiteSpace(a.Kind) ? old?.Kind ?? "" : a.Kind.Trim().ToLowerInvariant(),
            });
        }

        try
        {
            ConfigWriter.Write(fresh, config.ConfigPath);
            config.ReloadNow();
        }
        catch (Exception ex)
        {
            Log.Error("phone classify: config write failed — " + ex.Message);
            return Results.Problem("nu am putut salva: " + ex.Message);
        }
    }

    Log.Info($"Phone apps classified: {string.Join(", ", items.Select(a => $"{a.Name}={a.Class}"))}");
    return Results.Ok(new { saved = items.Count });
});

// ---- settings API (dashboard "Setări" page) ---------------------------------

app.MapGet("/api/config", () =>
{
    var c = config.Current;
    return Results.Json(new
    {
        projects = c.Projects.Select(p => new
        {
            name = p.Name,
            keywords = p.Keywords,
            claudeDirs = p.ClaudeDirs,
            browserProfiles = p.BrowserProfiles,
            apps = p.Apps,
            domains = p.Domains,
        }),
        classification = new
        {
            @default = c.Classification.Default,
            rules = c.Classification.Rules.Select(r => new { @class = r.Class, match = r.Match, value = r.Value }),
        },
        youtubeExceptions = new
        {
            titleKeywords = c.YoutubeExceptions.TitleKeywords,
            channels = c.YoutubeExceptions.Channels,
        },
        browser = new { processes = c.Browser.Processes },
        assignments = c.Assignments.Select(a => new { date = a.Date, match = a.Match, value = a.Value, project = a.Project, @class = a.Class, from = a.From, to = a.To }),
        profile = c.Profile,
        coach = c.Coach,
        configPath = config.ConfigPath,
        // optimistic-concurrency token: PUT-ul îl trimite înapoi; nepotrivire = 409
        version = File.GetLastWriteTimeUtc(config.ConfigPath).Ticks.ToString(),
    });
});

app.MapPut("/api/config", (ConfigUpdate update) =>
{
    try
    {
        var allowedClasses = new[] { "productive", "neutral", "unproductive" };
        var allowedMatches = new[] { "domain", "app", "title" };
        if (update.Projects.Any(p => string.IsNullOrWhiteSpace(p.Name)))
            return Results.BadRequest(new { error = "proiect fără nume" });
        if (update.Projects.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            return Results.BadRequest(new { error = "nume de proiect duplicat" });
        if (!allowedClasses.Contains(update.Classification.Default))
            return Results.BadRequest(new { error = "clasă default invalidă" });
        foreach (var r in update.Classification.Rules)
        {
            if (!allowedClasses.Contains(r.Class) || !allowedMatches.Contains(r.Match) || string.IsNullOrWhiteSpace(r.Value))
                return Results.BadRequest(new { error = $"regulă invalidă: {r.Class}/{r.Match}/'{r.Value}'" });
        }

        lock (ConfigWriter.SyncRoot) // serializează ÎNTREG ciclul load→modify→write
        {
        // gardă optimistă: dacă config-ul de pe disc s-a schimbat după ce pagina l-a
        // încărcat (popup „Marchează productiv", assign-day, coach), save-ul orb ar
        // șterge acele modificări — 409 și UI-ul cere reîncărcare
        if (!string.IsNullOrEmpty(update.Version)
            && File.GetLastWriteTimeUtc(config.ConfigPath).Ticks.ToString() != update.Version)
            return Results.Conflict(new { error = "Config-ul s-a schimbat între timp (popup/asignări/coach). Reîncarcă setările și aplică din nou modificarea." });

        // fresh copy from disk, replace ONLY the settings-page sections, validate, write
        var updated = TrackerConfig.Load(config.ConfigPath);
        updated.Projects = update.Projects.Select(p => new ProjectConfig
        {
            Name = p.Name.Trim(),
            Keywords = Clean(p.Keywords),
            ClaudeDirs = Clean(p.ClaudeDirs),
            BrowserProfiles = Clean(p.BrowserProfiles),
            Apps = Clean(p.Apps ?? new List<string>()),
            Domains = Clean(p.Domains ?? new List<string>()),
        }).ToList();
        updated.Classification = new ClassificationConfig
        {
            Default = update.Classification.Default,
            Rules = update.Classification.Rules
                .Select(r => new ClassificationRule { Class = r.Class, Match = r.Match, Value = r.Value.Trim() })
                .ToList(),
        };
        updated.YoutubeExceptions = new YoutubeExceptionsConfig
        {
            TitleKeywords = Clean(update.YoutubeExceptions.TitleKeywords),
            Channels = Clean(update.YoutubeExceptions.Channels),
        };
        if (update.Profile is not null) updated.Profile = update.Profile;
        if (update.Coach is not null) updated.Coach = update.Coach;
        // browser-suggest card (dashboard): only the process list is updatable from the UI
        if (update.BrowserProcesses is not null) updated.Browser.Processes = Clean(update.BrowserProcesses);
        updated.Validate();
        ConfigWriter.Write(updated, config.ConfigPath);
        config.ReloadNow();
        Log.Info("Config saved from the dashboard settings page");
        }
        return Results.Ok(new { saved = true });

        static List<string> Clean(List<string> xs) =>
            xs.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    catch (Exception ex)
    {
        Log.Error("Config save failed: " + ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

// adopția unui proiect „nesalvat" (nume apărut doar din date — vezi ReportService).
// ADITIV, nu overwrite: nu cere token de versiune, deci nu poate pierde modificări
// făcute între timp de popup/assign-day/coach (spre deosebire de PUT /api/config).
app.MapPost("/api/projects", (NewProjectDto req) =>
{
    try
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) return Results.BadRequest(new { error = "proiect fără nume" });

        lock (ConfigWriter.SyncRoot)
        {
            var updated = TrackerConfig.Load(config.ConfigPath);
            if (updated.Projects.Any(p => p.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
                return Results.Ok(new { created = false, name }); // idempotent: există deja

            updated.Projects.Add(new ProjectConfig
            {
                Name = name,
                Keywords = new List<string>(),
                ClaudeDirs = (req.ClaudeDirs ?? new List<string>())
                    .Select(x => x.Trim()).Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                BrowserProfiles = new List<string>(),
                Apps = new List<string>(),
                Domains = new List<string>(),
            });
            updated.Validate();
            ConfigWriter.Write(updated, config.ConfigPath);
            config.ReloadNow();
            Log.Info($"Project adopted from the dashboard: {name}");
        }
        return Results.Ok(new { created = true, name });
    }
    catch (Exception ex)
    {
        Log.Error("Project create failed: " + ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

// cwd-urile reale ale pseudo-proiectelor Claude, ca dialogul de adopție să precompleteze
// claude_dirs (in-memory în ClaudeModule: gol până la primul hook de după restart)
app.MapGet("/api/claude/cwds", () => Results.Json(claude.UnmappedCwds()));

// per-day project assignment ("today's Zoom → client X") — dashboard "doar ziua asta"
app.MapPost("/api/assign-day", (DayAssignDto req) =>
{
    try
    {
        var project = (req.Project ?? "").Trim();
        var cls = (req.Class ?? "").Trim();
        var from = (req.From ?? "").Trim();
        var to = (req.To ?? "").Trim();
        if (req.Match is not ("app" or "domain"))
            return Results.BadRequest(new { error = "match invalid (app|domain)" });
        if (string.IsNullOrWhiteSpace(req.Value) || (project.Length == 0 && cls.Length == 0))
            return Results.BadRequest(new { error = "value + (project și/sau class) obligatorii" });
        if (cls is not ("" or "productive" or "neutral" or "unproductive"))
            return Results.BadRequest(new { error = "class invalid" });
        if (!DateOnly.TryParseExact(req.Date, "yyyy-MM-dd", out _))
            return Results.BadRequest(new { error = "date invalid (yyyy-MM-dd)" });
        if (from.Length > 0 != (to.Length > 0))
            return Results.BadRequest(new { error = "interval incomplet: from și to merg împreună" });

        lock (ConfigWriter.SyncRoot)
        {
        var updated = TrackerConfig.Load(config.ConfigPath);
        // MERGE: „Zoom → ClientX azi" urmat de „Zoom → productiv azi" trebuie să coexiste;
        // intrările pe interval au cheia lărgită cu from/to (intervale diferite = intrări separate)
        var entry = updated.Assignments.FirstOrDefault(a =>
            a.Date == req.Date && a.Match == req.Match &&
            a.Value.Equals(req.Value, StringComparison.OrdinalIgnoreCase) &&
            a.From == from && a.To == to);
        if (entry is null)
        {
            entry = new AssignmentConfig { Date = req.Date, Match = req.Match, Value = req.Value.Trim(), From = from, To = to };
            updated.Assignments.Add(entry);
        }
        if (project.Length > 0) entry.Project = project;
        if (cls.Length > 0) entry.Class = cls;
        updated.Validate(); // prinde și HH:mm invalid, from>=to, intervale suprapuse
        ConfigWriter.Write(updated, config.ConfigPath);
        config.ReloadNow();
        var span = from.Length > 0 ? $" {from}-{to}" : "";
        Log.Info($"Day assignment: {req.Date}{span} {req.Match}:{req.Value} -> project='{entry.Project}' class='{entry.Class}'");
        }
        return Results.Ok(new { saved = true });
    }
    catch (Exception ex)
    {
        Log.Error("assign-day failed: " + ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

// part: "project" | "class" | null (= toată intrarea) — o intrare pe zi poate purta ambele;
// from/to identifică o intrare pe interval (lipsă = intrarea pe toată ziua)
app.MapDelete("/api/assign-day", (string date, string match, string value, string? part, string? from, string? to) =>
{
    try
    {
        var f = (from ?? "").Trim();
        var t = (to ?? "").Trim();
        lock (ConfigWriter.SyncRoot)
        {
        var updated = TrackerConfig.Load(config.ConfigPath);
        var entry = updated.Assignments.FirstOrDefault(a =>
            a.Date == date && a.Match == match && a.Value.Equals(value, StringComparison.OrdinalIgnoreCase) &&
            a.From == f && a.To == t);
        if (entry is null) return Results.NotFound(new { error = "atribuire inexistentă" });

        if (part == "project") entry.Project = "";
        else if (part == "class") entry.Class = "";
        if (part is null || (entry.Project.Length == 0 && entry.Class.Length == 0))
            updated.Assignments.Remove(entry);

        ConfigWriter.Write(updated, config.ConfigPath);

        config.ReloadNow();
        var span = f.Length > 0 ? $" {f}-{t}" : "";
        Log.Info($"Day assignment removed: {date}{span} {match}:{value} (part={part ?? "all"})");
        }
        return Results.Ok(new { removed = 1 });
    }
    catch (Exception ex)
    {
        Log.Error("assign-day delete failed: " + ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

// alocare „PE MINUTE" (UX-ul final, 2026-07-12): userul cere „N minute din WhatsApp →
// proiectul X / clasa Y"; serverul alege singur ferestrele orare reale (cronologic, din
// rularea efectivă, ocolind alocările existente) și le salvează ca [[assignments]] cu
// from/to — motorul de raport rămâne neschimbat, iar suma mutată e exactă.
// Idempotență: UI-ul trimite un requestId per rând; re-trimiterea aceluiași id
// (dublu-click, retry după răspuns pierdut) nu mai alocă încă o dată aceleași minute.
var minuteReqSeen = new HashSet<string>(StringComparer.Ordinal);
var minuteReqOrder = new Queue<string>();
app.MapPost("/api/assign-minutes", async (MinutesAssignDto req, CancellationToken ct) =>
{
    try
    {
        if (!string.IsNullOrEmpty(req.RequestId))
        {
            lock (minuteReqSeen)
            {
                if (minuteReqSeen.Contains(req.RequestId))
                    return Results.Ok(new { saved = true, duplicate = true });
            }
        }
        var project = (req.Project ?? "").Trim();
        var cls = (req.Class ?? "").Trim();
        if (req.Match is not ("app" or "domain"))
            return Results.BadRequest(new { error = "match invalid (app|domain)" });
        if (string.IsNullOrWhiteSpace(req.Value) || (project.Length == 0 && cls.Length == 0))
            return Results.BadRequest(new { error = "value + (project și/sau class) obligatorii" });
        if (cls is not ("" or "productive" or "neutral" or "unproductive"))
            return Results.BadRequest(new { error = "class invalid" });
        if (!DateOnly.TryParseExact(req.Date, "yyyy-MM-dd", out var day))
            return Results.BadRequest(new { error = "date invalid (yyyy-MM-dd)" });
        if (req.Minutes is < 1 or > 1440)
            return Results.BadRequest(new { error = "minutes trebuie să fie 1-1440" });

        var midnight = day.ToDateTime(TimeOnly.MinValue);
        var dayStart = new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
        var dayEnd = ReportService.LocalMidnight(midnight.AddDays(1)); // DST-safe
        var value = req.Value.Trim();
        var usage = await report.TargetUsageAsync(dayStart, dayEnd, req.Match, value, ct);

        var windowCount = 0;
        lock (ConfigWriter.SyncRoot)
        {
        var updated = TrackerConfig.Load(config.ConfigPath);
        var busy = updated.Assignments
            .Where(a => a.Date == req.Date && a.Match == req.Match
                        && a.Value.Equals(value, StringComparison.OrdinalIgnoreCase) && a.HasInterval)
            .Select(a => (S: ReportService.LocalInstant(midnight, a.From), E: ReportService.LocalInstant(midnight, a.To)))
            .OrderBy(x => x.S).ToList();

        // timp alocabil = rulare efectivă MINUS ferestrele deja alocate
        var free = new List<(DateTimeOffset S, DateTimeOffset E)>();
        foreach (var seg in usage)
        {
            var cur = seg.S;
            foreach (var b in busy)
            {
                if (b.E <= cur || b.S >= seg.E) continue;
                if (b.S > cur) free.Add((cur, b.S));
                if (b.E > cur) cur = b.E;
                if (cur >= seg.E) break;
            }
            if (cur < seg.E) free.Add((cur, seg.E));
        }

        var need = req.Minutes * 60.0;
        var avail = free.Sum(x => (x.E - x.S).TotalSeconds);
        if (avail + 0.5 < need)
            return Results.BadRequest(new
            {
                error = $"doar {(int)(avail / 60)} min disponibile pentru {value} pe {req.Date} (în afara alocărilor existente)",
                availableMinutes = (int)(avail / 60),
            });

        // cronologic, de la începutul zilei
        var picked = new List<(DateTimeOffset S, DateTimeOffset E)>();
        foreach (var x in free)
        {
            if (need <= 0.5) break;
            var take = Math.Min((x.E - x.S).TotalSeconds, need);
            picked.Add((x.S, x.S.AddSeconds(take)));
            need -= take;
        }
        // coalescăm ferestrele consecutive când golul dintre ele nu conține o alocare
        // existentă — pe zile TRECUTE golul nu mai poate primi activitate nouă. Pe ZIUA
        // CURENTĂ spanul lipit ar captura și folosirea ULTERIOARĂ a țintei în gol (raportul
        // aplică intervalul pe feliile viitoare), deci azi lipim doar golurile sub 1s
        // (care evită și suprapunerile din Floor/Ceil la salvare).
        var isPastDay = dayEnd <= DateTimeOffset.Now;
        var merged = new List<(DateTimeOffset S, DateTimeOffset E)>();
        foreach (var p in picked)
        {
            var glue = merged.Count > 0
                && (p.S - merged[^1].E < TimeSpan.FromSeconds(1)
                    || (isPastDay && !busy.Any(b => b.E > merged[^1].E && b.S < p.S)));
            if (glue) merged[^1] = (merged[^1].S, p.E > merged[^1].E ? p.E : merged[^1].E);
            else merged.Add(p);
        }

        static DateTimeOffset Floor(DateTimeOffset t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Offset);
        static DateTimeOffset Ceil(DateTimeOffset t) => t.Ticks % TimeSpan.TicksPerSecond == 0 ? t : Floor(t).AddSeconds(1);
        static string Hms(DateTimeOffset t)
        {
            var lt = t.ToLocalTime();
            return lt.Second == 0 ? lt.ToString("HH:mm") : lt.ToString("HH:mm:ss");
        }
        foreach (var (s0, e0) in merged)
        {
            // Ceil pe exact miezul nopții ar produce To="00:00" → Validate(from<to) pică
            // și refuză o alocare validă; clamp la ultima secundă a zilei
            var e1 = Ceil(e0);
            if (e1 >= dayEnd) e1 = dayEnd.AddSeconds(-1);
            updated.Assignments.Add(new AssignmentConfig
            {
                Date = req.Date, Match = req.Match, Value = value,
                From = Hms(Floor(s0)), To = Hms(e1),
                Project = project, Class = cls,
            });
        }
        updated.Validate();
        ConfigWriter.Write(updated, config.ConfigPath);
        config.ReloadNow();
        windowCount = merged.Count;
        }
        if (!string.IsNullOrEmpty(req.RequestId))
        {
            lock (minuteReqSeen)
            {
                // marcat DOAR după succes — un eșec real poate fi reîncercat cu același id
                if (minuteReqSeen.Add(req.RequestId))
                {
                    minuteReqOrder.Enqueue(req.RequestId);
                    while (minuteReqOrder.Count > 200) minuteReqSeen.Remove(minuteReqOrder.Dequeue());
                }
            }
        }
        Log.Info($"Minutes assignment: {req.Date} {req.Match}:{value} {req.Minutes}m -> project='{project}' class='{cls}' ({windowCount} ferestre)");
        return Results.Ok(new { saved = true, minutes = req.Minutes, windows = windowCount });
    }
    catch (Exception ex)
    {
        Log.Error("assign-minutes failed: " + ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    component = "tracker-daemon",
    milestone = "M4",
}));

// tray "pause popups" (M6) + manual control
app.MapPost("/popup/snooze", (int minutes) =>
{
    popupController.SnoozeFor(TimeSpan.FromMinutes(minutes));
    return Results.Ok(new { snoozedMinutes = minutes });
});

// tray "oprește tracking-ul" — a recorded pause, not a hole (see PauseService)
app.MapPost("/tracking/pause", (int minutes) =>
{
    pause.Start(minutes);
    return Results.Ok(new { paused = true, until = pause.Until });
});
app.MapPost("/tracking/resume", () =>
{
    pause.Resume();
    return Results.Ok(new { paused = false });
});

// F4 — focus mode (strict enforcement window)
app.MapPost("/focus/start", (int? minutes) =>
{
    focus.Start(minutes);
    return Results.Ok(new { active = true, until = focus.Until });
});
app.MapPost("/focus/stop", () =>
{
    focus.Stop();
    return Results.Ok(new { active = false });
});

// visual pipeline check without waiting for a real unproductive streak (auto-closes)
app.MapPost("/popup/test", () =>
{
    var c = config.Current;
    popupService.Show(
        new PopupModel(
            // a real rotating line, so /popup/test shows what the user will actually get
            Message: new PopupMessagePicker().Pick(new PopupMessageContext(
                Minutes: 12, Site: "YouTube", Now: DateTimeOffset.Now,
                TimesToday: 2, TotalTodaySeconds: 1800, TopPriority: null)),
            ContextText: "YouTube · 12 min",
            PostponeOptionsMinutes: c.Popup.PostponeOptionsMinutes,
            SureCooldownMinutes: c.Popup.SureCooldownMinutes),
        new PopupActions(_ => { }, () => { }));
    _ = Task.Delay(8000).ContinueWith(_ => popupService.Hide());
    return Results.Ok(new { shown = true });
});

// Claude Code hooks fire-and-forget events (decision #7) — never fail the caller
app.MapPost("/claude/event", async (HttpRequest req) =>
{
    // paused means paused: Claude work is activity too and must not be recorded either
    if (pause.IsActive) return Results.Ok();
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        claude.OnHookEvent(doc.RootElement);
    }
    catch (Exception ex)
    {
        Log.Error("claude/event failed: " + ex);
    }
    return Results.Ok();
});

// fire-and-forget mirror from the watcher (architecture §1.2)
app.MapPost("/window/state", (WindowState state) =>
{
    // the daemon's own windows (warn popup, coach toasts) must never count as the
    // user's activity: if the popup ever became foreground the engine would classify
    // it "neutral" and instantly dismiss it — keep the last REAL window instead
    if (!state.App.Equals("Tracker.Daemon.exe", StringComparison.OrdinalIgnoreCase))
        windowStore.Update(state);
    return Results.Ok();
});

// extension heartbeats (architecture §1.4) — stored for the engine + forwarded to the web bucket
app.MapPost("/browser/heartbeat", async (BrowserHeartbeat hb) =>
{
    // replay din coada de retry a extensiei (daemonul a fost jos): poartă timestampul
    // ORIGINAL — e istorie pentru bucket, nu stare LIVE (altfel ar otrăvi BestFor/AnyAudible)
    var isReplay = hb.Timestamp != default && DateTimeOffset.UtcNow - hb.Timestamp > TimeSpan.FromSeconds(10);
    if (!isReplay) browserStore.Update(hb);
    var data = new Dictionary<string, object?>
    {
        ["url"] = hb.Url,
        ["title"] = hb.Title,
        ["audible"] = hb.Audible,
        ["incognito"] = hb.Incognito,
        // tabCount deliberately NOT stored: it changes constantly and would break
        // aw-server's heartbeat merging (identical data within pulsetime)
    };
    if (!string.IsNullOrEmpty(hb.Channel)) data["channel"] = hb.Channel;
    if (!string.IsNullOrEmpty(hb.Profile)) data["profile"] = hb.Profile;
    if (!string.IsNullOrEmpty(hb.Browser)) data["browser"] = hb.Browser;

    // exactly ONE writer at a time: with several profiles open, each extension instance
    // may claim focus — forwarding all of them interleaves the bucket and no event ever
    // merges (every event ends up with duration 0). Only the instance matching the
    // foreground window is written.
    var (curWin, winAge) = windowStore.Snapshot();
    var winFresh = curWin is not null && DateTimeOffset.UtcNow - winAge < TimeSpan.FromSeconds(15);

    bool shouldWrite;
    if (isReplay)
    {
        // gate-ul live (fereastra din prim-plan) nu se poate aplica retroactiv unui
        // heartbeat vechi — flagul de focus capturat de extensie la momentul respectiv decide
        shouldWrite = hb.Focused;
    }
    else if (!winFresh)
    {
        shouldWrite = hb.Focused; // watcher down — degrade to the extension's own focus flag
    }
    else if (!config.Current.Browser.Processes.Contains(curWin!.App, StringComparer.OrdinalIgnoreCase))
    {
        shouldWrite = false; // a non-browser app is in front: no browser instance owns the time
    }
    else
    {
        var best = browserStore.BestFor(curWin.Title, curWin.App, TimeSpan.FromSeconds(90), curWin.Aumid);
        shouldWrite = best is not null
            && string.Equals(best.Browser, hb.Browser, StringComparison.OrdinalIgnoreCase)
            && string.Equals(best.Profile, hb.Profile, StringComparison.OrdinalIgnoreCase);

        // single-profile browser in the foreground: nothing to interleave with, so accept
        // even when the title proof fails (fix for Edge's blind minutes, 2026-07-10) —
        // the strict proof stays whenever the SAME browser has 2+ fresh profiles
        if (!shouldWrite
            && ForegroundMatchesBrowser(curWin.App, hb.Browser)
            && browserStore.CanClaimForegroundWindow(hb, curWin.Aumid)
            && browserStore.IsOnlyFreshInstanceOfBrowser(hb.Browser, hb.Profile, TimeSpan.FromSeconds(90)))
        {
            shouldWrite = true;
        }
    }

    // "Oprește tracking-ul": no URL/title of any tab is recorded while paused. Replays are
    // dropped too — their timestamp may fall inside the pause window.
    if (pause.IsActive) shouldWrite = false;

    if (shouldWrite)
    {
        await store.HeartbeatAsync(AwBuckets.Web(host), data, config.Current.Browser.PulsetimeSeconds,
            isReplay ? hb.Timestamp : null);
    }
    // F4: during focus mode the extension enforces this blocklist instantly (decision #5 two-way channel)
    return Results.Json(new
    {
        focus = new
        {
            active = focus.IsActive && config.Current.Focus.CloseTabs,
            blockedDomains = focus.BlockedDomains(),
        },
    });

    // the extension reports only "edge" (UA has Edg/) or "chrome" (any other Chromium)
    static bool ForegroundMatchesBrowser(string app, string? browser) => browser?.ToLowerInvariant() switch
    {
        "edge" => app.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase),
        "chrome" => !app.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
});

// live state for debugging, the popup and the dashboard
app.MapGet("/state", () =>
{
    var (window, lastUpdate) = windowStore.Snapshot();
    return Results.Json(new
    {
        window,
        windowLastUpdate = lastUpdate,
        engine = engine.Snapshot,
        focus = new { active = focus.IsActive, until = focus.Until },
        paused = new { active = pause.IsActive, until = pause.Until },
    });
});

var url = $"http://127.0.0.1:{bridgePort}";
Log.Info($"Bridge listening on {url} — dashboard + /api/report + /api/config + /api/0 shim + bridge endpoints.");
app.Run(url);

// ---- settings API DTOs ----
internal sealed record ProjectDto(
    string Name, List<string> Keywords, List<string> ClaudeDirs, List<string> BrowserProfiles,
    List<string>? Apps = null, List<string>? Domains = null);
internal sealed record NewProjectDto(string? Name, List<string>? ClaudeDirs = null);
internal sealed record RuleDto(string Class, string Match, string Value);
internal sealed record ClassificationDto(string Default, List<RuleDto> Rules);
internal sealed record YoutubeDto(List<string> TitleKeywords, List<string> Channels);
internal sealed record ConfigUpdate(
    List<ProjectDto> Projects, ClassificationDto Classification, YoutubeDto YoutubeExceptions,
    ProfileConfig? Profile = null, CoachConfig? Coach = null, List<string>? BrowserProcesses = null,
    string? Version = null);
internal sealed record DayAssignDto(
    string Date, string Match, string Value, string? Project = null, string? Class = null,
    string? From = null, string? To = null);
internal sealed record PhoneAppRuleDto(string Name, string Class, string? Project = null, string? Kind = null);
internal sealed record PhoneClassifyDto(List<PhoneAppRuleDto>? Apps);
internal sealed record PhoneAppUsageDto(string Name, int Minutes);
internal sealed record PhoneUsageDto(
    string From, string To, int TotalMinutes,
    string? Device = null, List<PhoneAppUsageDto>? Apps = null,
    int? Pickups = null, int? Notifications = null, string? Source = null);
internal sealed record MinutesAssignDto(
    string Date, string Match, string Value, int Minutes, string? Project = null, string? Class = null,
    string? RequestId = null);
