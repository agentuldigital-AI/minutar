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
///  - fallback: jsonl mtime watcher on ~/.claude/projects (folder = encoded cwd,
///    filename = session_id) for sessions whose hooks don't fire.
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
    private FileSystemWatcher? _jsonlWatcher;
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
        StartJsonlFallback();
        Log.Info("Claude module running (hooks endpoint + jsonl fallback, attention tick 1s)");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await AttentionTickAsync(ct);
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
        _jsonlWatcher?.Dispose();
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

    private void StartJsonlFallback()
    {
        var cfg = _config.Current;
        if (!cfg.Claude.JsonlFallback) return;
        // projects_dir gol în config = locația standard a transcriptelor Claude Code
        _projectsDir = string.IsNullOrWhiteSpace(cfg.Claude.ProjectsDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects")
            : cfg.Claude.ProjectsDir;
        if (!Directory.Exists(_projectsDir))
        {
            Log.Warn($"Claude projects dir not found ({_projectsDir}) — jsonl fallback off");
            return;
        }

        _jsonlWatcher = new FileSystemWatcher(_projectsDir, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _jsonlWatcher.Changed += OnJsonl;
        _jsonlWatcher.Created += OnJsonl;
        _jsonlWatcher.EnableRaisingEvents = true;
    }

    private void OnJsonl(object sender, FileSystemEventArgs e)
    {
        try
        {
            var sessionId = Path.GetFileNameWithoutExtension(e.Name ?? "");
            // the encoded cwd is always the FIRST folder under projects_dir — deeper
            // levels are subagent/workflow transcripts belonging to the same project
            var rel = Path.GetRelativePath(_projectsDir, e.FullPath);
            if (rel.StartsWith("..", StringComparison.Ordinal)) return;
            var encodedDir = rel.Split('\\', '/')[0];
            if (sessionId.Length == 0 || encodedDir.Length == 0) return;

            var now = DateTimeOffset.UtcNow;
            string? cached;
            lock (_lock)
            {
                if (_jsonlLastSent.TryGetValue(sessionId, out var at) && now - at < JsonlMinInterval) return;
                _jsonlLastSent[sessionId] = now;
                _sessionProject.TryGetValue(sessionId, out cached);
            }

            // resolve the REAL cwd from the transcript itself, so the jsonl fallback produces
            // the SAME project name as the hooks (no more encoded-dir pseudo-projects)
            var project = cached ?? TryReadCwdProject(e.FullPath) ?? MapEncodedDir(encodedDir);
            lock (_lock)
            {
                _sessionProject[sessionId] = project;
            }
            EmitWork(project, sessionId);
        }
        catch (Exception ex)
        {
            Log.Warn("jsonl fallback event failed: " + ex.Message);
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
        return Path.GetFileName(cwd.TrimEnd('\\', '/'));
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
