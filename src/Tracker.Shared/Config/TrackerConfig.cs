using Tomlyn;
using Tomlyn.Model;

namespace Tracker.Shared.Config;

/// <summary>
/// Strongly-typed model of config/tracker.toml — the single source of truth (locked decision #9).
/// TOML keys are snake_case; properties map via <see cref="TomlOptions"/>.
/// </summary>
public sealed class TrackerConfig
{
    public ServerConfig Server { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public AfkConfig Afk { get; set; } = new();
    public WindowConfig Window { get; set; } = new();
    public BrowserConfig Browser { get; set; } = new();
    public ClaudeConfig Claude { get; set; } = new();
    public PopupConfig Popup { get; set; } = new();
    public VideoConfig Video { get; set; } = new();
    public AttributionConfig Attribution { get; set; } = new();
    public List<ProjectConfig> Projects { get; set; } = new();
    public List<AssignmentConfig> Assignments { get; set; } = new();
    public ClassificationConfig Classification { get; set; } = new();
    public YoutubeExceptionsConfig YoutubeExceptions { get; set; } = new();
    public SupervisorConfig Supervisor { get; set; } = new();
    public GoalsConfig Goals { get; set; } = new();
    public FocusConfig Focus { get; set; } = new();
    public ProfileConfig Profile { get; set; } = new();
    public CoachConfig Coach { get; set; } = new();

    public static TrackerConfig Load(string path)
    {
        var text = File.ReadAllText(path);
        var cfg = Toml.ToModel<TrackerConfig>(text, path, TomlOptions.Default);
        cfg.Validate();
        return cfg;
    }

    /// <summary>
    /// Rejects values that would busy-loop or crash the capture loops when hot-reloaded
    /// (tournament review finding). ConfigProvider keeps the last GOOD config on throw.
    /// Public so the settings API can validate BEFORE writing the file.
    /// </summary>
    public void Validate()
    {
        Require(Server.BridgePort is >= 1 and <= 65535, "server.bridge_port must be 1-65535");
        foreach (var a in Assignments)
        {
            Require(a.Match is "app" or "domain", "assignments.match must be app|domain");
            Require(!string.IsNullOrWhiteSpace(a.Value), "assignments need a value");
            Require(!string.IsNullOrWhiteSpace(a.Project) || !string.IsNullOrWhiteSpace(a.Class),
                "assignments need a project and/or a class");
            Require(a.Class is "" or "productive" or "neutral" or "unproductive",
                "assignments.class must be productive|neutral|unproductive");
            Require(DateOnly.TryParseExact(a.Date, "yyyy-MM-dd", out _), "assignments.date must be yyyy-MM-dd");
            Require(a.From.Length == 0 == (a.To.Length == 0), "assignments need both from and to, or neither");
            if (a.HasInterval)
            {
                // HH:mm:ss e emis de alocarea „pe minute" (tăietura cade rar fix pe minut)
                var formats = new[] { "HH:mm", "HH:mm:ss" };
                var fOk = TimeOnly.TryParseExact(a.From, formats, out var f);
                var tOk = TimeOnly.TryParseExact(a.To, formats, out var t);
                Require(fOk && tOk, "assignments.from/to must be HH:mm[:ss]");
                Require(f < t, "assignments.from must be before to");
            }
        }
        // intervals on the same target+day must not overlap (which one wins would be ambiguous)
        foreach (var g in Assignments.Where(a => a.HasInterval)
                     .GroupBy(a => (a.Date, a.Match, Value: a.Value.ToLowerInvariant())))
        {
            var sorted = g.OrderBy(a => a.From, StringComparer.Ordinal).ToList();
            for (var i = 1; i < sorted.Count; i++)
                Require(string.CompareOrdinal(sorted[i].From, sorted[i - 1].To) >= 0,
                    $"assignments intervals overlap for {g.Key.Value} on {g.Key.Date}");
        }
        Require(Afk.PollSeconds >= 1, "afk.poll_seconds must be >= 1");
        Require(Afk.TimeoutSeconds >= 10, "afk.timeout_seconds must be >= 10");
        Require(Window.PollSeconds >= 1, "window.poll_seconds must be >= 1");
        Require(Window.PulsetimeSeconds > Window.PollSeconds, "window.pulsetime_seconds must exceed window.poll_seconds");
        Require(Browser.HeartbeatSeconds >= 5, "browser.heartbeat_seconds must be >= 5");
        Require(Browser.PulsetimeSeconds > Browser.HeartbeatSeconds, "browser.pulsetime_seconds must exceed browser.heartbeat_seconds");
        Require(Claude.WorkPulsetimeSeconds >= 30, "claude.work_pulsetime_seconds must be >= 30");
        Require(Claude.AttentionPulsetimeSeconds >= 5, "claude.attention_pulsetime_seconds must be >= 5");
        Require(Popup.GraceSeconds >= 5, "popup.grace_seconds must be >= 5");
        Require(Popup.PostponeOptionsMinutes.All(m => m >= 1), "popup.postpone_options_minutes must all be >= 1");
        Require(Popup.RenagMinutesDefault >= 1, "popup.renag_minutes_default must be >= 1");
        Require(Popup.SureCooldownMinutes >= 1, "popup.sure_cooldown_minutes must be >= 1");
        Require(Attribution.HoldSeconds >= 0, "attribution.hold_seconds must be >= 0");
        Require(Supervisor.CheckSeconds >= 5, "supervisor.check_seconds must be >= 5");
        Require(Goals.StreakProductiveMinutes >= 1, "goals.streak_productive_minutes must be >= 1");
        Require(Coach.MinMinutesBetweenNudges >= 5, "coach.min_minutes_between_nudges must be >= 5");
        Require(Coach.RuleCooldownMinutes >= 10, "coach.rule_cooldown_minutes must be >= 10");
        Require(Coach.FlowMinutes >= 5, "coach.flow_minutes must be >= 5");
        Require(Coach.ToastSeconds is >= 5 and <= 60, "coach.toast_seconds must be 5-60");
        Require(Coach.UnproductiveMinutes >= 3, "coach.unproductive_minutes must be >= 3");
        Require(Coach.MaxSwitchesPerHour >= 10, "coach.max_switches_per_hour must be >= 10");
        Require(Coach.NoBreakHours >= 0.5, "coach.no_break_hours must be >= 0.5");
        Require(Coach.DeadlineDriftDays >= 1, "coach.deadline_drift_days must be >= 1");
        Require(Focus.GraceSeconds >= 1, "focus.grace_seconds must be >= 1");
        Require(Focus.CountdownSeconds >= 3, "focus.countdown_seconds must be >= 3");
        Require(Focus.DefaultMinutes >= 1, "focus.default_minutes must be >= 1");

        static void Require(bool ok, string message)
        {
            if (!ok) throw new InvalidDataException("tracker.toml invalid: " + message);
        }
    }
}

public sealed class ServerConfig
{
    public string AwUrl { get; set; } = "http://localhost:5600";
    public int BridgePort { get; set; } = 5601;
    public string BucketHost { get; set; } = "";

    /// <summary>Suffix for bucket IDs; falls back to the machine name when unset.</summary>
    public string ResolveBucketHost() =>
        string.IsNullOrWhiteSpace(BucketHost) ? Environment.MachineName : BucketHost;
}

/// <summary>
/// Own event storage (plan docs/PLAN-2026-07-10-remove-activitywatch.md). STARTUP-ONLY:
/// the daemon opens the DB and binds Kestrel once — hot-reload deliberately ignores
/// these keys (a config edit takes effect at the next daemon restart).
/// </summary>
public sealed class StorageConfig
{
    /// <summary>Empty = %LOCALAPPDATA%\TimeTracker\events.db. Override for dev instances (--db beats both).</summary>
    public string DbPath { get; set; } = "";

    /// <summary>
    /// Parity-gate tee (temporary, plan M4): when set to the real aw-server URL, /api/0
    /// writes are applied locally AND forwarded to aw-server, while reads are proxied to
    /// aw-server (it stays the source of truth until cutover). Empty = standalone mode.
    /// </summary>
    public string TeeAwUrl { get; set; } = "";

    /// <summary>Resolved database path (config or default), before any --db CLI override.</summary>
    public string ResolveDbPath() => string.IsNullOrWhiteSpace(DbPath)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeTracker", "events.db")
        : DbPath;
}

public sealed class AfkConfig
{
    public int TimeoutSeconds { get; set; } = 180;
    public int PollSeconds { get; set; } = 5;
}

public sealed class WindowConfig
{
    public int PollSeconds { get; set; } = 1;
    public int PulsetimeSeconds { get; set; } = 5;
}

public sealed class BrowserConfig
{
    public int HeartbeatSeconds { get; set; } = 60;
    public int PulsetimeSeconds { get; set; } = 80;
    public List<string> Processes { get; set; } = new() { "chrome.exe", "msedge.exe" };
}

public sealed class ClaudeConfig
{
    public string ProjectsDir { get; set; } = "";
    public List<string> DesktopProcesses { get; set; } = new() { "Claude.exe" };
    public int WorkPulsetimeSeconds { get; set; } = 150;
    public int AttentionPulsetimeSeconds { get; set; } = 15;
    public bool JsonlFallback { get; set; } = true;
}

public sealed class PopupConfig
{
    public int GraceSeconds { get; set; } = 30;
    public List<int> PostponeOptionsMinutes { get; set; } = new() { 5, 10, 30 };
    public int RenagMinutesDefault { get; set; } = 10;
    public int SureCooldownMinutes { get; set; } = 60;
    public List<string> QuietHours { get; set; } = new();
}

public sealed class VideoConfig
{
    public bool AudibleFocusedCountsActive { get; set; } = true;
}

public sealed class AttributionConfig
{
    public int HoldSeconds { get; set; } = 20;
}

public sealed class ProjectConfig
{
    public string Name { get; set; } = "";
    public List<string> Keywords { get; set; } = new();
    public List<string> ClaudeDirs { get; set; } = new();

    /// <summary>
    /// Decision #12: extension profile labels and/or AppUserModelID fragments.
    /// For browser windows a profile match beats keyword attribution.
    /// </summary>
    public List<string> BrowserProfiles { get; set; } = new();

    /// <summary>Explicit exe names pinned to this project — beat profile AND keywords.</summary>
    public List<string> Apps { get; set; } = new();

    /// <summary>Explicit domains pinned to this project — beat profile AND keywords.</summary>
    public List<string> Domains { get; set; } = new();
}

public sealed class ClassificationConfig
{
    public string Default { get; set; } = "neutral";
    public List<ClassificationRule> Rules { get; set; } = new();
}

/// <summary>
/// One per-day project assignment ("Zoom on 2026-07-10 → ClientX"): overrides every
/// attribution rule, but ONLY for that calendar day (local time). Written by the
/// dashboard's "doar ziua asta" option; reports apply it retroactively like everything else.
/// </summary>
public sealed class AssignmentConfig
{
    /// <summary>Local calendar day, yyyy-MM-dd.</summary>
    public string Date { get; set; } = "";

    /// <summary>app | domain</summary>
    public string Match { get; set; } = "app";

    public string Value { get; set; } = "";

    /// <summary>Empty = no project override for that day.</summary>
    public string Project { get; set; } = "";

    /// <summary>Empty = no class override; else productive|neutral|unproductive, that day only
    /// (ex. WhatsApp e neproductiv ca regulă, dar pe ziua cu clientul devine productiv).</summary>
    public string Class { get; set; } = "";

    /// <summary>Optional interval start, local "HH:mm". Empty = the whole day. With an interval
    /// set, the override applies only inside [from, to) — an interval beats a whole-day entry
    /// for the same day ("Zoom 14:00-15:30 → ClientX", restul zilei rămâne unde era).</summary>
    public string From { get; set; } = "";

    /// <summary>Optional interval end, local "HH:mm"; required when from is set.</summary>
    public string To { get; set; } = "";

    /// <summary>True when this entry targets a time interval instead of the whole day.</summary>
    public bool HasInterval => From.Length > 0;
}

public sealed class ClassificationRule
{
    /// <summary>productive | neutral | unproductive</summary>
    public string Class { get; set; } = "neutral";

    /// <summary>domain | app | title</summary>
    public string Match { get; set; } = "title";

    public string Value { get; set; } = "";
}

public sealed class YoutubeExceptionsConfig
{
    public List<string> TitleKeywords { get; set; } = new();
    public List<string> Channels { get; set; } = new();
}

public sealed class FocusConfig
{
    /// <summary>Grace before the countdown popup during focus mode (vs popup.grace_seconds normally).</summary>
    public int GraceSeconds { get; set; } = 5;

    /// <summary>Cancellable countdown shown before an unproductive APP is closed.</summary>
    public int CountdownSeconds { get; set; } = 10;

    public bool CloseApps { get; set; } = true;

    /// <summary>During focus, the extension closes tabs on unproductive domains instantly.</summary>
    public bool CloseTabs { get; set; } = true;

    public int DefaultMinutes { get; set; } = 25;

    /// <summary>Never force-closed, whatever the rules say.</summary>
    public List<string> NeverClose { get; set; } = new()
    {
        "explorer.exe", "claude.exe", "chrome.exe", "msedge.exe",
        "Tracker.Daemon.exe", "Tracker.Watcher.exe", "Tracker.Supervisor.exe", "aw-server.exe",
    };
}

/// <summary>My Profile (Coach v0): who the user is and how they want to be coached.</summary>
public sealed class ProfileConfig
{
    /// <summary>"De ce muncesc" — woven into coach messages.</summary>
    public string Why { get; set; } = "";

    /// <summary>coach | direct | funny | calm | data_driven (one or more).</summary>
    public List<string> MotivationStyles { get; set; } = new() { "coach" };

    /// <summary>Things the coach must never do (used by the LLM layer in v1).</summary>
    public List<string> Dislikes { get; set; } = new();

    public string WorkStart { get; set; } = "09:00";
    public string WorkEnd { get; set; } = "18:00";
    public string Lunch { get; set; } = "13:00-14:00";
    public bool WorkWeekends { get; set; } = false;

    /// <summary>e.g. ["09:00-11:00", "14:00-16:00"] — no nudges here while productive.</summary>
    public List<string> FocusIntervals { get; set; } = new();

    public List<GoalItem> ObjectiveList { get; set; } = new();
}

public sealed class GoalItem
{
    public string Title { get; set; } = "";

    /// <summary>personal | professional</summary>
    public string Kind { get; set; } = "professional";

    /// <summary>yyyy-MM-dd, empty = no deadline.</summary>
    public string Deadline { get; set; } = "";

    /// <summary>Optional link to a tracker project (deadline-drift rule).</summary>
    public string Project { get; set; } = "";
}

/// <summary>Coach v0 rule thresholds — all local, no LLM.</summary>
public sealed class CoachConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Global anti-spam: minimum minutes between any two nudges.</summary>
    public int MinMinutesBetweenNudges { get; set; } = 20;

    /// <summary>Per-rule cooldown (same rule won't re-fire sooner).</summary>
    public int RuleCooldownMinutes { get; set; } = 90;

    /// <summary>≥ N continuous productive minutes on one project = Flow → total silence.</summary>
    public int FlowMinutes { get; set; } = 20;

    /// <summary>Toast auto-dismiss.</summary>
    public int ToastSeconds { get; set; } = 12;

    // rule thresholds
    public bool RuleUnproductive { get; set; } = true;
    public int UnproductiveMinutes { get; set; } = 15;

    public bool RuleContextSwitching { get; set; } = true;
    public int MaxSwitchesPerHour { get; set; } = 120;

    public bool RuleMainNotStarted { get; set; } = true;
    public string MainProjectCheckAt { get; set; } = "12:00";

    public bool RuleNoBreak { get; set; } = true;
    public double NoBreakHours { get; set; } = 2.0;

    public bool RuleDeadlineDrift { get; set; } = true;
    public int DeadlineDriftDays { get; set; } = 7;
}

public sealed class GoalsConfig
{
    /// <summary>A day counts toward the streak when it has at least this many PRODUCTIVE minutes.</summary>
    public int StreakProductiveMinutes { get; set; } = 60;
}

public sealed class SupervisorConfig
{
    /// <summary>Empty = %LOCALAPPDATA%\Programs\ActivityWatch\aw-server\aw-server.exe</summary>
    public string AwServerExe { get; set; } = "";

    /// <summary>Empty = auto-resolve (published sibling dir, then dev bin path).</summary>
    public string WatcherExe { get; set; } = "";
    public string DaemonExe { get; set; } = "";

    public int CheckSeconds { get; set; } = 10;
}

internal static class TomlOptions
{
    public static TomlModelOptions Default => new()
    {
        ConvertPropertyName = ToSnakeCase,
        ConvertFieldName = ToSnakeCase,
        IgnoreMissingProperties = true,
    };

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
