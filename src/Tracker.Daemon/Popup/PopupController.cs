using Microsoft.Extensions.Hosting;
using Tracker.Daemon.Engine;
using Tracker.Daemon.Focus;
using Tracker.Daemon.State;
using Tracker.Shared.Config;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Popup;

/// <summary>
/// Popup state machine (decision #4, architecture §1.3): fires when the engine reports
/// unproductive+active continuously past the grace period, honoring postpone/snooze,
/// per-activity cooldowns ("I'm sure"), quiet hours, and implicit re-nag when the
/// popup is dismissed without choosing anything.
/// </summary>
public sealed class PopupController : BackgroundService
{
    private readonly ConfigProvider _config;
    private readonly RulesEngineService _engine;
    private readonly PopupService _popup;
    private readonly FocusService _focus;
    private readonly WindowStateStore _window;
    private readonly Coach.DayStateStore _days;
    private readonly PopupMessagePicker _messages = new();
    private readonly object _lock = new();

    private int _unproductiveStreak;
    private DateTimeOffset _snoozeUntil;
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = new();
    // per-activity tally for the day, so a message can say "third time today" and how long
    // in total — reset on the first popup of a new local day
    private readonly Dictionary<string, (int Times, int Seconds)> _todayTally = new(StringComparer.Ordinal);
    private string _tallyDate = "";

    public PopupController(
        ConfigProvider config, RulesEngineService engine, PopupService popup,
        FocusService focus, WindowStateStore window, Coach.DayStateStore days)
    {
        _config = config;
        _engine = engine;
        _popup = popup;
        _focus = focus;
        _window = window;
        _days = days;
    }

    /// <summary>Tray "pause popups" (M6) sets this via the bridge.</summary>
    public void SnoozeFor(TimeSpan duration)
    {
        lock (_lock)
        {
            _snoozeUntil = DateTimeOffset.UtcNow + duration;
        }
        Log.Info($"Popups snoozed until {_snoozeUntil.ToLocalTime():HH:mm}");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _popup.Start();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.Error("Popup tick failed: " + ex);
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

    private void Tick()
    {
        var cfg = _config.Current;
        var snap = _engine.Snapshot;
        var now = DateTimeOffset.UtcNow;

        var isUnproductive = snap is { Active: true, Class: "unproductive" };
        if (!isUnproductive)
        {
            _unproductiveStreak = 0;
            if (_popup.IsVisible) _popup.Hide(); // the user already moved on — dismiss
            return;
        }

        _unproductiveStreak++;
        var key = CooldownKey(snap!);
        var focusOn = _focus.IsActive;

        if (!focusOn) // focus mode overrides snoozes/cooldowns — it is strict by design
        {
            lock (_lock)
            {
                if (_snoozeUntil > now) return;
                if (_cooldowns.TryGetValue(key, out var until) && until > now) return;
            }
        }
        if (_popup.IsVisible) return;
        if (_unproductiveStreak < (focusOn ? cfg.Focus.GraceSeconds : cfg.Popup.GraceSeconds)) return;
        if (!focusOn && IsQuietNow(cfg, DateTimeOffset.Now)) return;

        ShowPopup(cfg, snap!, key);
    }

    private void ShowPopup(TrackerConfig cfg, EngineSnapshot snap, string key)
    {
        // F4: during focus, non-browser unproductive apps get a cancellable close countdown.
        // Browsers are NEVER closed as apps — their tabs are handled by the extension blocklist.
        var isBrowser = AttributionEngine.IsBrowser(cfg, snap.App);
        var neverClose = cfg.Focus.NeverClose.Contains(snap.App, StringComparer.OrdinalIgnoreCase);
        int? countdown = _focus.IsActive && cfg.Focus.CloseApps && !isBrowser && !neverClose
            ? cfg.Focus.CountdownSeconds
            : null;
        var (winAtShow, _) = _window.Snapshot();
        var hwndAtShow = winAtShow?.Hwnd ?? 0;
        var pidAtShow = winAtShow is not null && winAtShow.App.Equals(snap.App, StringComparison.OrdinalIgnoreCase)
            ? winAtShow.Pid
            : 0;

        Log.Info($"Popup firing: {_unproductiveStreak}s unproductive on {snap.App} (rule {snap.MatchedRule ?? "?"}){(countdown is not null ? $" — FOCUS close in {countdown}s" : "")}");
        // one quiet line of fact: what and for how long. The process name and the matched
        // rule stay in the log — they are for debugging, not for the person being nudged.
        var site = PopupMessagePicker.SiteNameOf(snap.MatchedRule, snap.App);
        var mins = _unproductiveStreak / 60;
        var duration = mins >= 1 ? $"{mins} min" : $"{_unproductiveStreak}s";
        var model = new PopupModel(
            Message: BuildMessage(snap, key),
            ContextText: $"{site} · {duration}",
            PostponeOptionsMinutes: cfg.Popup.PostponeOptionsMinutes,
            SureCooldownMinutes: cfg.Popup.SureCooldownMinutes,
            CountdownSeconds: countdown);

        var actionTaken = false;
        var actions = new PopupActions(
            Postpone: minutes =>
            {
                actionTaken = true;
                lock (_lock)
                {
                    _snoozeUntil = DateTimeOffset.UtcNow.AddMinutes(minutes);
                }
                Log.Info($"Popup postponed {minutes} min");
            },
            Sure: () =>
            {
                actionTaken = true;
                lock (_lock)
                {
                    _cooldowns[key] = DateTimeOffset.UtcNow.AddMinutes(cfg.Popup.SureCooldownMinutes);
                }
                Log.Info($"Popup cooldown {cfg.Popup.SureCooldownMinutes} min for '{key}'");
            },
            OnCountdownExpired: countdown is null || pidAtShow == 0 ? null : () =>
            {
                actionTaken = true; // enforcement takes over — no implicit re-nag snooze
                _ = EnforceCloseAsync(snap.App, hwndAtShow, pidAtShow);
            },
            // the X: closing without choosing anything. Still a floor, or the popup would
            // reappear on the next tick — but the shortest one, and nothing is promised.
            Dismiss: () =>
            {
                actionTaken = true;
                var minutes = _config.Current.Popup.DismissMinutes;
                lock (_lock)
                {
                    _snoozeUntil = DateTimeOffset.UtcNow.AddMinutes(minutes);
                }
                Log.Info($"Popup dismissed via X — quiet for {minutes} min");
            });

        _popup.Show(model, actions, onClosed: () =>
        {
            if (actionTaken) return;
            // dismissed without choosing (Alt+F4 etc.) — implicit re-nag interval
            lock (_lock)
            {
                _snoozeUntil = DateTimeOffset.UtcNow.AddMinutes(_config.Current.Popup.RenagMinutesDefault);
            }
            Log.Info($"Popup dismissed without action — re-nag in {_config.Current.Popup.RenagMinutesDefault} min");
        });
    }

    /// <summary>
    /// The rotating line shown above the measured facts. Never throws and never returns
    /// empty: it is now the popup's headline, so a failure here degrades to a plain sentence
    /// rather than leaving the window without a subject.
    /// </summary>
    private string BuildMessage(EngineSnapshot snap, string key)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var today = now.ToString("yyyy-MM-dd");
            int times, seconds;
            lock (_lock)
            {
                if (_tallyDate != today)
                {
                    _todayTally.Clear();
                    _tallyDate = today;
                }
                var prev = _todayTally.GetValueOrDefault(key);
                (times, seconds) = (prev.Times + 1, prev.Seconds + _unproductiveStreak);
                _todayTally[key] = (times, seconds);
            }

            // the first unfinished priority of the day, if the user set any
            string? priority = null;
            try
            {
                priority = _days.Today().Priorities.FirstOrDefault(p => !p.Done && p.Text.Length > 0)?.Text;
            }
            catch (Exception ex)
            {
                Log.Warn("Popup message: day state unreadable — " + ex.Message);
            }

            return _messages.Pick(new PopupMessageContext(
                Minutes: _unproductiveStreak / 60,
                Site: PopupMessagePicker.SiteNameOf(snap.MatchedRule, snap.App),
                Now: now,
                TimesToday: times,
                TotalTodaySeconds: seconds,
                TopPriority: priority));
        }
        catch (Exception ex)
        {
            Log.Warn("Popup message pick failed — " + ex.Message);
            var mins = _unproductiveStreak / 60;
            return mins >= 1 ? $"{mins} minute pe o activitate neproductivă." : "Activitate neproductivă.";
        }
    }

    /// <summary>Runs after the countdown expired: re-verifies the target is STILL the same
    /// foreground pid and still unproductive before closing anything (F4 safety).</summary>
    private async Task EnforceCloseAsync(string app, long hwnd, int pid)
    {
        var (cur, _) = _window.Snapshot();
        if (cur is null || cur.Pid != pid)
        {
            Log.Info($"Focus: '{app}' no longer in foreground — close skipped");
            return;
        }
        if (_engine.Snapshot is not { Class: "unproductive" })
        {
            Log.Info($"Focus: '{app}' no longer unproductive — close skipped");
            return;
        }
        await AppCloser.CloseAsync(hwnd, pid, app, p =>
        {
            var (w, _) = _window.Snapshot();
            return w?.Pid == p;
        });
    }

    private static string CooldownKey(EngineSnapshot snap) => snap.MatchedRule ?? snap.App;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static bool IsQuietNow(TrackerConfig cfg, DateTimeOffset localNow)
    {
        foreach (var range in cfg.Popup.QuietHours)
        {
            var parts = range.Split('-');
            if (parts.Length != 2) continue;
            if (!TimeSpan.TryParse(parts[0], out var start) || !TimeSpan.TryParse(parts[1], out var end)) continue;
            var t = localNow.TimeOfDay;
            var inRange = start <= end
                ? t >= start && t < end
                : t >= start || t < end; // overnight wrap, e.g. 22:30-08:00
            if (inRange) return true;
        }
        return false;
    }
}
