using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Tracker.Daemon.State;
using Tracker.Shared.Aw;
using Tracker.Shared.Config;
using Tracker.Shared.Storage;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Claude;

/// <summary>
/// Claude Code per-project attribution (decision #7, architecture §1.3):
///  - claude-work: heartbeat per hook event keyed {project, session_id}, pulsetime ~150 s,
///    counted regardless of focus;
///  - claude-attention: emitted while the desktop app is the foreground window AND a
///    last-interacted session exists (UserPromptSubmit / Notification);
///  - fallback: scanare periodică a mtime-urilor din ~/.claude/projects (folder = encoded cwd,
///    filename = session_id) pentru sesiunile ale căror hook-uri nu se declanșează.
/// </summary>
public sealed class ClaudeModule : BackgroundService
{
    private static readonly TimeSpan AttentionEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan JsonlMinInterval = TimeSpan.FromSeconds(15);

    private readonly ConfigProvider _config;
    private readonly WindowStateStore _window;
    private readonly IEventStore _store;
    private readonly string _host;

    private readonly object _lock = new();
    private (string Project, string SessionId, DateTimeOffset At)? _lastInteracted;
    private readonly Dictionary<string, DateTimeOffset> _jsonlLastSent = new();
    private readonly Dictionary<string, string> _sessionProject = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _workLastEmit = new(StringComparer.OrdinalIgnoreCase);
    // pseudo-proiect (nume de folder, fallback-ul din MapProject) → cwd-ul REAL, ca
    // dialogul de adopție din dashboard să poată precompleta claude_dirs. In-memory:
    // se repopulează de la primul hook/jsonl al sesiunii după restart.
    private readonly Dictionary<string, string> _unmappedCwd = new(StringComparer.OrdinalIgnoreCase);
    private string _projectsDir = "";
    private DateTimeOffset _lastAttention;

    public ClaudeModule(ConfigProvider config, WindowStateStore window, IEventStore store, string host)
    {
        _config = config;
        _window = window;
        _store = store;
        _host = host;
    }

    /// <summary>Called from POST /claude/event with the raw hook payload.</summary>
    public void OnHookEvent(JsonElement payload)
    {
        var sessionId = payload.TryGetProperty("session_id", out var s) ? s.GetString() ?? "" : "";
        var cwd = payload.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "";
        var eventName = payload.TryGetProperty("hook_event_name", out var e) ? e.GetString() ?? "" : "";
        if (sessionId.Length == 0 && cwd.Length == 0) return;

        var project = MapProject(cwd);
        EmitWork(project, sessionId);

        // interaction signals drive the attention metric (decision #7)
        if (eventName is "UserPromptSubmit" or "Notification" or "SessionStart")
        {
            lock (_lock)
            {
                _lastInteracted = (project, sessionId, DateTimeOffset.UtcNow);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = EnsureBucketsAsync(ct); // in parallel — events queue in the resilient client until ready
        Log.Info("Claude module running (hooks endpoint + scanare jsonl la 10s, attention tick 1s)");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await AttentionTickAsync(ct);
                ScanJsonl();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Claude attention tick failed: " + ex);
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
                await _store.EnsureBucketAsync(AwBuckets.ClaudeWork(_host), AwBuckets.ClaudeWorkType, ct);
                await _store.EnsureBucketAsync(AwBuckets.ClaudeAttention(_host), AwBuckets.ClaudeAttentionType, ct);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Log.Warn("aw-server unreachable, retrying claude bucket creation in 10s ...");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    private async Task AttentionTickAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        var now = DateTimeOffset.UtcNow;
        if (now - _lastAttention < AttentionEvery) return;

        var (win, lastUpdate) = _window.Snapshot();
        if (win is null || now - lastUpdate > TimeSpan.FromSeconds(15)) return;
        if (!cfg.Claude.DesktopProcesses.Contains(win.App, StringComparer.OrdinalIgnoreCase)) return;

        (string Project, string SessionId, DateTimeOffset At)? last;
        lock (_lock)
        {
            last = _lastInteracted;
        }
        if (last is null) return;

        await _store.HeartbeatAsync(
            AwBuckets.ClaudeAttention(_host),
            new Dictionary<string, object?> { ["project"] = last.Value.Project, ["session_id"] = last.Value.SessionId },
            cfg.Claude.AttentionPulsetimeSeconds,
            ct: ct);
        _lastAttention = now;
    }

    // --- jsonl mtime fallback (research §6) ---------------------------------

    /// <summary>
    /// Scanează mtime-urile transcriptelor, în loc să asculte notificări de fișier.
    ///
    /// A fost un FileSystemWatcher recursiv, și a trebuit schimbat: Claude Code scrie în
    /// transcript continuu cât produce text, iar fiecare scriere ridica notificări duse pe fire
    /// din pool. La o sesiune lungă — a fost măsurată una de peste 50 MB — potopul de notificări
    /// înfometa exact pool-ul din care serverul HTTP își ia firele.
    ///
    /// Costul unei verificări nu depinde de CÂT se scrie, ci doar de câte fișiere există — o
    /// enumerare de metadate, la câteva secunde. Fallback-ul n-are nevoie de precizie mai bună:
    /// e plasa pentru sesiunile ale căror hook-uri nu se declanșează, iar acolo contează că
    /// timpul se vede, nu că se vede în aceeași secundă.
    /// </summary>
    private static readonly TimeSpan JsonlScanEvery = TimeSpan.FromSeconds(10);

    private DateTimeOffset _lastJsonlScan;
    private readonly Dictionary<string, DateTime> _jsonlSeenMtime = new(StringComparer.OrdinalIgnoreCase);

    private void ScanJsonl()
    {
        var cfg = _config.Current;
        if (!cfg.Claude.JsonlFallback) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastJsonlScan < JsonlScanEvery) return;
        _lastJsonlScan = now;

        // projects_dir gol în config = locația standard a transcriptelor Claude Code
        _projectsDir = string.IsNullOrWhiteSpace(cfg.Claude.ProjectsDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects")
            : cfg.Claude.ProjectsDir;
        if (!Directory.Exists(_projectsDir)) return;

        foreach (var path in Directory.EnumerateFiles(_projectsDir, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                var mtime = File.GetLastWriteTimeUtc(path);
                // prima scanare doar reține pozițiile: la pornire, toate fișierele ar părea
                // proaspete, iar daemonul ar emite muncă pentru sesiuni închise de săptămâni
                var stiut = _jsonlSeenMtime.TryGetValue(path, out var vazut);
                _jsonlSeenMtime[path] = mtime;
                if (!stiut || mtime <= vazut) continue;

                var sessionId = Path.GetFileNameWithoutExtension(path);
                // cwd-ul codificat e mereu PRIMUL folder sub projects_dir — nivelurile mai
                // adânci sunt transcripte de subagent din același proiect
                var rel = Path.GetRelativePath(_projectsDir, path);
                if (rel.StartsWith("..", StringComparison.Ordinal)) continue;
                var encodedDir = rel.Split('\\', '/')[0];
                if (sessionId.Length == 0 || encodedDir.Length == 0) continue;

                string? cached;
                lock (_lock)
                {
                    if (_jsonlLastSent.TryGetValue(sessionId, out var at) && now - at < JsonlMinInterval) continue;
                    _jsonlLastSent[sessionId] = now;
                    _sessionProject.TryGetValue(sessionId, out cached);
                }

                // cwd-ul REAL se citește din transcript, ca fallback-ul să producă ACELAȘI nume
                // de proiect ca hook-urile (fără pseudo-proiecte din folderul codificat)
                var project = cached ?? TryReadCwdProject(path) ?? MapEncodedDir(encodedDir);
                lock (_lock)
                {
                    _sessionProject[sessionId] = project;
                }
                EmitWork(project, sessionId);
            }
            catch (Exception ex)
            {
                Log.Warn("jsonl fallback scan failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Emits a claude-work heartbeat carrying an EXPLICIT backfilled duration: the time
    /// since this PROJECT's previous event (capped at pulsetime). All sessions share one
    /// bucket, so with 2+ concurrent sessions the interleaved heartbeats have different
    /// data and the store's merge-against-last never fires — 0-duration events would sum
    /// to ~0 worked time. With the stamped [now-gap, now] span each insert carries its own
    /// coverage; for a single sequential session the merge behaves exactly as before.
    /// </summary>
    private void EmitWork(string project, string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        var pulse = _config.Current.Claude.WorkPulsetimeSeconds;
        double dur = 0;
        lock (_lock)
        {
            if (_workLastEmit.TryGetValue(project, out var prev))
            {
                var gap = (now - prev).TotalSeconds;
                if (gap > 0 && gap <= pulse) dur = gap; // gap peste pulsetime = pauză reală, nu se punte
            }
            _workLastEmit[project] = now;
        }
        _ = _store.HeartbeatAsync(
            AwBuckets.ClaudeWork(_host),
            new Dictionary<string, object?> { ["project"] = project, ["session_id"] = sessionId },
            pulse,
            now.AddSeconds(-dur),
            dur);
    }

    /// <summary>Reads the "cwd" field from the first lines of a Claude transcript jsonl.</summary>
    private string? TryReadCwdProject(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            for (var i = 0; i < 5; i++)
            {
                var line = sr.ReadLine();
                if (line is null) break;
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var cwd = c.GetString();
                        if (!string.IsNullOrEmpty(cwd)) return MapProject(cwd);
                    }
                }
                catch (JsonException)
                {
                    // not a JSON line — keep scanning
                }
            }
        }
        catch (Exception)
        {
            // locked/partial file — the hooks path covers it
        }
        return null;
    }

    // --- cwd → project mapping ----------------------------------------------

    private string MapProject(string cwd)
    {
        if (cwd.Length == 0) return "(unknown)";
        var norm = Normalize(cwd);
        foreach (var p in _config.Current.Projects)
        {
            foreach (var dir in p.ClaudeDirs)
            {
                var d = Normalize(dir);
                if (norm.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                    norm.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))
                    return p.Name;
            }
        }
        // niciun claude_dirs nu se potrivește → pseudo-proiect din numele folderului;
        // reținem cwd-ul real pentru adopția din dashboard
        var fallback = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        if (fallback.Length > 0)
        {
            lock (_lock) _unmappedCwd[fallback] = cwd.TrimEnd('\\', '/');
        }
        return fallback;
    }

    /// <summary>cwd-urile reale ale pseudo-proiectelor văzute de la pornirea daemonului
    /// (proiect → cwd), pentru precompletarea lui claude_dirs la adopție.</summary>
    public Dictionary<string, string> UnmappedCwds()
    {
        lock (_lock) return new Dictionary<string, string>(_unmappedCwd, StringComparer.OrdinalIgnoreCase);
    }

    private string MapEncodedDir(string encodedDir)
    {
        // ~/.claude/projects encodes the cwd as path with ':' and '\' replaced by '-'
        foreach (var p in _config.Current.Projects)
        {
            foreach (var dir in p.ClaudeDirs)
            {
                var enc = dir.Replace(":", "-").Replace("\\", "-").Replace("/", "-");
                if (encodedDir.StartsWith(enc, StringComparison.OrdinalIgnoreCase))
                    return p.Name;
            }
        }
        return encodedDir;
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimEnd('/');
}
