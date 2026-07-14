using System.Text;

namespace Tracker.Shared.Config;

/// <summary>
/// Writes tracker.toml back in CANONICAL form (dashboard settings page, decision #9 UI).
/// Atomic write + timestamped backup (config/backups, newest 20 kept). Custom hand-written
/// comments are not preserved — the canonical section comments are regenerated instead.
/// </summary>
public static class ConfigWriter
{
    private static readonly object Lock = new();

    /// <summary>Gate pentru ÎNTREG ciclul Load→modify→Write al config-ului: fără el, două
    /// scrieri concurente (MarkProductive vs Settings vs assign-day) fac last-writer-wins
    /// și una se pierde. Write() ia același lock (reentrant pe același thread).</summary>
    public static object SyncRoot => Lock;

    public static void Write(TrackerConfig cfg, string path)
    {
        lock (Lock)
        {
            Backup(path);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(cfg), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
    }

    private static void Backup(string path)
    {
        if (!File.Exists(path)) return;
        var dir = Path.Combine(Path.GetDirectoryName(path)!, "backups");
        Directory.CreateDirectory(dir);
        File.Copy(path, Path.Combine(dir, $"tracker-{DateTime.Now:yyyyMMdd-HHmmss}.toml"), overwrite: true);
        foreach (var old in Directory.GetFiles(dir, "tracker-*.toml").OrderByDescending(f => f).Skip(20))
        {
            File.Delete(old);
        }
    }

    private static string Serialize(TrackerConfig c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# config/tracker.toml - single source of truth (decision #9). Hot-reloaded in ~1s.");
        sb.AppendLine("# Saved from the dashboard settings page in canonical form; hand edits are fine too,");
        sb.AppendLine("# but custom comments are replaced on the next dashboard save.");
        sb.AppendLine("# The popup's \"mark as productive\" button also writes here (classification rules /");
        sb.AppendLine("# youtube exceptions) — everything is visible and editable in the dashboard settings.");
        sb.AppendLine();
        sb.AppendLine("[server]");
        sb.AppendLine($"aw_url      = {Q(c.Server.AwUrl)}");
        sb.AppendLine($"bridge_port = {c.Server.BridgePort}");
        sb.AppendLine($"bucket_host = {Q(c.Server.BucketHost)}");
        sb.AppendLine();
        sb.AppendLine("[storage]");
        sb.AppendLine($"db_path    = {Q(c.Storage.DbPath)}");
        sb.AppendLine($"tee_aw_url = {Q(c.Storage.TeeAwUrl)}");
        sb.AppendLine();
        sb.AppendLine("[afk]");
        sb.AppendLine($"timeout_seconds = {c.Afk.TimeoutSeconds}");
        sb.AppendLine($"poll_seconds    = {c.Afk.PollSeconds}");
        sb.AppendLine();
        sb.AppendLine("[window]");
        sb.AppendLine($"poll_seconds      = {c.Window.PollSeconds}");
        sb.AppendLine($"pulsetime_seconds = {c.Window.PulsetimeSeconds}");
        sb.AppendLine();
        sb.AppendLine("[browser]");
        sb.AppendLine($"heartbeat_seconds = {c.Browser.HeartbeatSeconds}");
        sb.AppendLine($"pulsetime_seconds = {c.Browser.PulsetimeSeconds}");
        sb.AppendLine($"processes         = {Arr(c.Browser.Processes)}");
        sb.AppendLine();
        sb.AppendLine("[claude]");
        sb.AppendLine($"projects_dir                = {Lit(c.Claude.ProjectsDir)}");
        sb.AppendLine($"desktop_processes           = {Arr(c.Claude.DesktopProcesses)}");
        sb.AppendLine($"work_pulsetime_seconds      = {c.Claude.WorkPulsetimeSeconds}");
        sb.AppendLine($"attention_pulsetime_seconds = {c.Claude.AttentionPulsetimeSeconds}");
        sb.AppendLine($"jsonl_fallback              = {(c.Claude.JsonlFallback ? "true" : "false")}");
        sb.AppendLine();
        sb.AppendLine("[popup]");
        sb.AppendLine($"grace_seconds            = {c.Popup.GraceSeconds}");
        sb.AppendLine($"postpone_options_minutes = [{string.Join(", ", c.Popup.PostponeOptionsMinutes)}]");
        sb.AppendLine($"renag_minutes_default    = {c.Popup.RenagMinutesDefault}");
        sb.AppendLine($"sure_cooldown_minutes    = {c.Popup.SureCooldownMinutes}");
        sb.AppendLine($"quiet_hours              = {Arr(c.Popup.QuietHours)}");
        sb.AppendLine();
        sb.AppendLine("[video]");
        sb.AppendLine($"audible_focused_counts_active = {(c.Video.AudibleFocusedCountsActive ? "true" : "false")}");
        sb.AppendLine();
        sb.AppendLine("[attribution]");
        sb.AppendLine($"hold_seconds = {c.Attribution.HoldSeconds}");
        sb.AppendLine();
        sb.AppendLine("[supervisor]");
        sb.AppendLine($"aw_server_exe = {Lit(c.Supervisor.AwServerExe)}");
        sb.AppendLine($"watcher_exe   = {Lit(c.Supervisor.WatcherExe)}");
        sb.AppendLine($"daemon_exe    = {Lit(c.Supervisor.DaemonExe)}");
        sb.AppendLine($"check_seconds = {c.Supervisor.CheckSeconds}");
        sb.AppendLine();
        sb.AppendLine("[goals]");
        sb.AppendLine($"streak_productive_minutes = {c.Goals.StreakProductiveMinutes}");
        sb.AppendLine();
        sb.AppendLine("[focus]");
        sb.AppendLine($"grace_seconds     = {c.Focus.GraceSeconds}");
        sb.AppendLine($"countdown_seconds = {c.Focus.CountdownSeconds}");
        sb.AppendLine($"close_apps        = {(c.Focus.CloseApps ? "true" : "false")}");
        sb.AppendLine($"close_tabs        = {(c.Focus.CloseTabs ? "true" : "false")}");
        sb.AppendLine($"default_minutes   = {c.Focus.DefaultMinutes}");
        sb.AppendLine($"never_close       = {Arr(c.Focus.NeverClose)}");

        sb.AppendLine();
        sb.AppendLine("[profile]");
        sb.AppendLine($"why               = {Q(c.Profile.Why)}");
        sb.AppendLine($"motivation_styles = {Arr(c.Profile.MotivationStyles)}");
        sb.AppendLine($"dislikes          = {Arr(c.Profile.Dislikes)}");
        sb.AppendLine($"work_start        = {Q(c.Profile.WorkStart)}");
        sb.AppendLine($"work_end          = {Q(c.Profile.WorkEnd)}");
        sb.AppendLine($"lunch             = {Q(c.Profile.Lunch)}");
        sb.AppendLine($"work_weekends     = {(c.Profile.WorkWeekends ? "true" : "false")}");
        sb.AppendLine($"focus_intervals   = {Arr(c.Profile.FocusIntervals)}");
        foreach (var g in c.Profile.ObjectiveList)
        {
            sb.AppendLine();
            sb.AppendLine("[[profile.objective_list]]");
            sb.AppendLine($"title    = {Q(g.Title)}");
            sb.AppendLine($"kind     = {Q(g.Kind)}");
            sb.AppendLine($"deadline = {Q(g.Deadline)}");
            sb.AppendLine($"project  = {Q(g.Project)}");
        }
        sb.AppendLine();
        sb.AppendLine("[coach]");
        sb.AppendLine($"enabled                    = {(c.Coach.Enabled ? "true" : "false")}");
        sb.AppendLine($"min_minutes_between_nudges = {c.Coach.MinMinutesBetweenNudges}");
        sb.AppendLine($"rule_cooldown_minutes      = {c.Coach.RuleCooldownMinutes}");
        sb.AppendLine($"flow_minutes               = {c.Coach.FlowMinutes}");
        sb.AppendLine($"toast_seconds              = {c.Coach.ToastSeconds}");
        sb.AppendLine($"rule_unproductive          = {(c.Coach.RuleUnproductive ? "true" : "false")}");
        sb.AppendLine($"unproductive_minutes       = {c.Coach.UnproductiveMinutes}");
        sb.AppendLine($"rule_context_switching     = {(c.Coach.RuleContextSwitching ? "true" : "false")}");
        sb.AppendLine($"max_switches_per_hour      = {c.Coach.MaxSwitchesPerHour}");
        sb.AppendLine($"rule_main_not_started      = {(c.Coach.RuleMainNotStarted ? "true" : "false")}");
        sb.AppendLine($"main_project_check_at      = {Q(c.Coach.MainProjectCheckAt)}");
        sb.AppendLine($"rule_no_break              = {(c.Coach.RuleNoBreak ? "true" : "false")}");
        sb.AppendLine($"no_break_hours             = {c.Coach.NoBreakHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine($"rule_deadline_drift        = {(c.Coach.RuleDeadlineDrift ? "true" : "false")}");
        sb.AppendLine($"deadline_drift_days        = {c.Coach.DeadlineDriftDays}");

        foreach (var p in c.Projects)
        {
            sb.AppendLine();
            sb.AppendLine("[[projects]]");
            sb.AppendLine($"name             = {Q(p.Name)}");
            sb.AppendLine($"keywords         = {Arr(p.Keywords)}");
            sb.AppendLine($"claude_dirs      = {ArrLit(p.ClaudeDirs)}");
            sb.AppendLine($"browser_profiles = {Arr(p.BrowserProfiles)}");
            sb.AppendLine($"apps             = {Arr(p.Apps)}");
            sb.AppendLine($"domains          = {Arr(p.Domains)}");
        }

        sb.AppendLine();
        sb.AppendLine("[classification]");
        sb.AppendLine($"default = {Q(c.Classification.Default)}");
        foreach (var r in c.Classification.Rules)
        {
            sb.AppendLine();
            sb.AppendLine("[[classification.rules]]");
            sb.AppendLine($"class = {Q(r.Class)}");
            sb.AppendLine($"match = {Q(r.Match)}");
            sb.AppendLine($"value = {Q(r.Value)}");
        }

        sb.AppendLine();
        sb.AppendLine("[youtube_exceptions]");
        sb.AppendLine($"title_keywords = {Arr(c.YoutubeExceptions.TitleKeywords)}");
        sb.AppendLine($"channels       = {Arr(c.YoutubeExceptions.Channels)}");

        // per-day project assignments ("today's Zoom → client X") — override rules for
        // that day only; written by the dashboard's "doar ziua asta" dropdown option
        foreach (var a in c.Assignments)
        {
            sb.AppendLine();
            sb.AppendLine("[[assignments]]");
            sb.AppendLine($"date    = {Q(a.Date)}");
            sb.AppendLine($"match   = {Q(a.Match)}");
            sb.AppendLine($"value   = {Q(a.Value)}");
            if (a.From.Length > 0) sb.AppendLine($"from    = {Q(a.From)}");
            if (a.To.Length > 0) sb.AppendLine($"to      = {Q(a.To)}");
            if (a.Project.Length > 0) sb.AppendLine($"project = {Q(a.Project)}");
            if (a.Class.Length > 0) sb.AppendLine($"class   = {Q(a.Class)}");
        }
        return sb.ToString();
    }

    private static string Q(string v)
    {
        // TOML basic strings may not contain raw control chars (a stray newline in a saved
        // title/keyword would corrupt the file and every component would fail to start)
        var sb = new StringBuilder(v.Length + 2).Append('"');
        foreach (var ch in v)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(ch)) sb.Append("\\u").Append(((int)ch).ToString("X4"));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Literal (single-quoted) string — used for Windows paths; falls back to escaping.</summary>
    private static string Lit(string v) =>
        v.Contains('\'') || v.Any(char.IsControl) ? Q(v) : "'" + v + "'";

    private static string Arr(IEnumerable<string> xs) => "[" + string.Join(", ", xs.Select(Q)) + "]";
    private static string ArrLit(IEnumerable<string> xs) => "[" + string.Join(", ", xs.Select(Lit)) + "]";
}
