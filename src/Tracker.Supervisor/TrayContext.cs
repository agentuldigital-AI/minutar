using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;
using Tracker.Shared.Config;
using Tracker.Shared.Logging;

namespace Tracker.Supervisor;

/// <summary>
/// Tray supervisor (decision #10, architecture §1.6): starts aw-server → watcher →
/// daemon, watchdogs them with health probes + backoff restarts, and offers the
/// tray menu (status, pause popups, open dashboards, restart, exit).
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _checkTimer;
    private readonly List<ManagedProcess> _components = new();
    private readonly TrackerConfig _cfg;
    private readonly string _configPath;
    private readonly UiState _uiState;
    private readonly StatusPoller _poller;
    private readonly MiniBarForm _miniBar;
    private volatile bool _sessionLocked;

    public TrayContext(TrackerConfig cfg, string configPath, bool uiTestMode = false)
    {
        _cfg = cfg;
        _configPath = configPath;
        _uiState = UiState.Load();

        // pe lock screen watcher-ul nu poate captura ferestre → mirror-ul devine stale →
        // probe-ul de sănătate ar intra în KILL-LOOP deși watcher-ul e perfect sănătos.
        // Cât sesiunea e blocată/deconectată, probe-ul de prospețime se suspendă.
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;

        var awUrl = cfg.Server.AwUrl.TrimEnd('/');
        var bridge = $"http://127.0.0.1:{cfg.Server.BridgePort}";

        if (!uiTestMode)
        {
            // cutover 2026-07-12: aw-server rulează DOAR cât timp umbra e activă
            // ([storage] tee_aw_url setat) — retragerea finală = golește cheia + restart.
            // Ordinea = ordinea de pornire: daemonul (store-ul) înaintea watcher-ului.
            if (!string.IsNullOrWhiteSpace(cfg.Storage.TeeAwUrl))
            {
                var teeUrl = cfg.Storage.TeeAwUrl.TrimEnd('/');
                _components.Add(new ManagedProcess(new ComponentSpec(
                    "aw-server", ExeResolver.AwServer(cfg), "",
                    () => ProbeAsync($"{teeUrl}/api/0/info"))));
            }
            _components.Add(new ManagedProcess(new ComponentSpec(
                "daemon", ExeResolver.Component(cfg, "Tracker.Daemon", cfg.Supervisor.DaemonExe, _configPath),
                $"--config \"{_configPath}\"",
                () => ProbeAsync($"{bridge}/health"))));
            _components.Add(new ManagedProcess(new ComponentSpec(
                "watcher", ExeResolver.Component(cfg, "Tracker.Watcher", cfg.Supervisor.WatcherExe, _configPath),
                $"--config \"{_configPath}\"",
                () => _sessionLocked ? Task.FromResult(true) : WatcherHealthyAsync(bridge))));
        }

        var menu = BuildMenu(bridge, awUrl);
        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Minutar",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // live status: dynamic tray dot + taskbar mini-bar (decizia utilizatorului, v1.2)
        _miniBar = new MiniBarForm(_uiState, menu, onClick: () => OpenUrl(bridge),
            tooltipProvider: () => TodayTooltipAsync(bridge));
        _poller = new StatusPoller(bridge);
        _poller.Updated += status =>
        {
            TrayIconFactory.Apply(_tray, status);
            _miniBar.UpdateStatus(status);
        };

        foreach (var c in _components) c.EnsureStarted();

        _checkTimer = new System.Windows.Forms.Timer { Interval = Math.Max(5, cfg.Supervisor.CheckSeconds) * 1000 };
        _checkTimer.Tick += async (_, _) =>
        {
            foreach (var c in _components)
            {
                await c.CheckAsync();
            }
        };
        if (!uiTestMode) _checkTimer.Start();
        Log.Info(uiTestMode
            ? "Supervisor in UI-TEST mode (no child processes)"
            : "Supervisor running — components: " + string.Join(", ", _components.Select(c => c.Name)));
    }

    private ContextMenuStrip BuildMenu(string bridge, string awUrl)
    {
        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem("Status componente");
        menu.Items.Add(statusItem);
        menu.Opening += (_, _) =>
        {
            statusItem.DropDownItems.Clear();
            foreach (var c in _components)
                statusItem.DropDownItems.Add(new ToolStripMenuItem($"{c.Name}: {c.Status}") { Enabled = false });
        };

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Deschide dashboard", null, (_, _) => OpenUrl(bridge));
        menu.Items.Add(new ToolStripSeparator());
        var miniBarItem = new ToolStripMenuItem("Mini-bar pe taskbar") { CheckOnClick = true };
        menu.Opening += (_, _) => miniBarItem.Checked = _uiState.MiniBarVisible;
        miniBarItem.CheckedChanged += (_, _) =>
        {
            _uiState.MiniBarVisible = miniBarItem.Checked;
            _uiState.Save();
            _miniBar.RedockNow();
        };
        menu.Items.Add(miniBarItem);
        menu.Items.Add("Pauză popup-uri 30 min", null, (_, _) => _ = SnoozeAsync(bridge, 30));
        menu.Items.Add("Pauză popup-uri 60 min", null, (_, _) => _ = SnoozeAsync(bridge, 60));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("🎯 Focus 25 min", null, (_, _) => _ = FocusAsync(bridge, "start?minutes=25"));
        menu.Items.Add("🎯 Focus 50 min", null, (_, _) => _ = FocusAsync(bridge, "start?minutes=50"));
        menu.Items.Add("Oprește focus", null, (_, _) => _ = FocusAsync(bridge, "stop"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart componente", null, (_, _) =>
        {
            foreach (var c in _components)
            {
                c.Stop();
                c.EnsureStarted();
            }
        });
        menu.Items.Add("Ieșire (oprește tot)", null, (_, _) => ExitAll());
        return menu;
    }

    /// <summary>Mini-bar hover tooltip: today's per-class totals + focus score.</summary>
    private static async Task<string?> TodayTooltipAsync(string bridge)
    {
        var json = await Http.GetStringAsync($"{bridge}/api/report");
        using var doc = JsonDocument.Parse(json);
        var byClass = doc.RootElement.GetProperty("totals").GetProperty("byClass");
        double Sec(string k) => byClass.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        static string Fmt(double s) => s >= 3600 ? $"{(int)(s / 3600)}h {(int)(s % 3600 / 60)}m" : $"{(int)(s / 60)}m";
        var text = $"Azi: {Fmt(Sec("productive"))} productiv · {Fmt(Sec("neutral"))} neutru · {Fmt(Sec("unproductive"))} neproductiv";
        if (doc.RootElement.TryGetProperty("focus", out var f)
            && f.ValueKind == JsonValueKind.Object
            && f.TryGetProperty("score", out var score)
            && score.ValueKind == JsonValueKind.Number)
        {
            text += $"\nFocus score: {score.GetInt32()}/100";
        }
        return text;
    }

    private static async Task<bool> ProbeAsync(string url)
    {
        var resp = await Http.GetAsync(url);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Watcher is healthy when the daemon's window mirror is fresh; if the daemon
    /// itself is down we can't judge, so only the process liveness check applies.</summary>
    private static async Task<bool> WatcherHealthyAsync(string bridge)
    {
        try
        {
            var json = await Http.GetStringAsync($"{bridge}/state");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("windowLastUpdate", out var lu)) return true;
            var last = lu.GetDateTimeOffset();
            return DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(60);
        }
        catch
        {
            return true; // daemon unreachable — its own probe handles that
        }
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        _sessionLocked = e.Reason switch
        {
            Microsoft.Win32.SessionSwitchReason.SessionLock
                or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
                or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect => true,
            Microsoft.Win32.SessionSwitchReason.SessionUnlock
                or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
                or Microsoft.Win32.SessionSwitchReason.RemoteConnect => false,
            _ => _sessionLocked,
        };
        Log.Info($"Session {(_sessionLocked ? "locked" : "unlocked")} — watcher freshness probe {(_sessionLocked ? "suspended" : "resumed")}");
    }

    private static async Task FocusAsync(string bridge, string pathAndQuery)
    {
        try
        {
            await Http.PostAsync($"{bridge}/focus/{pathAndQuery}", null);
        }
        catch (Exception ex)
        {
            Log.Warn("Focus toggle failed: " + ex.Message);
        }
    }

    private static async Task SnoozeAsync(string bridge, int minutes)
    {
        try
        {
            await Http.PostAsync($"{bridge}/popup/snooze?minutes={minutes}", null);
        }
        catch (Exception ex)
        {
            Log.Warn("Snooze failed: " + ex.Message);
        }
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void ExitAll()
    {
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        _checkTimer.Stop();
        _poller.Dispose();
        _miniBar.Close();
        foreach (var c in _components) c.Stop();
        _tray.Visible = false;
        Log.Info("Supervisor exiting — all components stopped");
        ExitThread();
    }
}
