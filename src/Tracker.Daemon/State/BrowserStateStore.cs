namespace Tracker.Daemon.State;

/// <summary>
/// One heartbeat from a per-profile extension instance (architecture §1.4).
/// AnyAudible = any tab in that profile is audible (video rule needs it even when the
/// audible tab is not the active one).
/// </summary>
public sealed record BrowserHeartbeat(
    string Url,
    string Title,
    bool Audible,
    bool AnyAudible,
    bool Incognito,
    int TabCount,
    string? Channel,
    string? Profile,
    string? Email,
    bool Focused,
    string? Browser,
    DateTimeOffset Timestamp = default);

/// <summary>Latest state per extension instance (browser × profile).</summary>
public sealed class BrowserStateStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (BrowserHeartbeat Hb, DateTimeOffset At)> _byInstance = new();

    public void Update(BrowserHeartbeat hb)
    {
        var key = $"{hb.Browser}|{hb.Profile}";
        lock (_lock)
        {
            _byInstance[key] = (hb, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// True when the given (browser, profile) is the ONLY fresh instance of that browser —
    /// a single-profile browser has no one to interleave with, so its heartbeats can be
    /// accepted even without the window-title proof (fix for Edge's blind minutes,
    /// 2026-07-10). With 2+ fresh profiles of the same browser the strict proof stays.
    /// </summary>
    public bool IsOnlyFreshInstanceOfBrowser(string? browser, string? profile, TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        lock (_lock)
        {
            var fresh = _byInstance.Values
                .Where(v => v.At >= cutoff &&
                            string.Equals(v.Hb.Browser, browser, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return fresh.Count > 0 && fresh.All(v =>
                string.Equals(v.Hb.Profile, profile, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Freshest heartbeat from a profile whose window has OS focus, within maxAge.</summary>
    public BrowserHeartbeat? CurrentFocused(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        lock (_lock)
        {
            return _byInstance.Values
                .Where(v => v.At >= cutoff && v.Hb.Focused)
                .OrderByDescending(v => v.At)
                .Select(v => v.Hb)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Best instance for the CURRENT foreground window: the tab whose title matches the
    /// window title wins (deterministic — kills the multi-profile focus flicker); falls
    /// back to the freshest focused heartbeat. When the foreground app identifies a
    /// browser, only instances of THAT browser compete — an identical tab title open in
    /// the other browser must not steal the profile (flip-flop fix).
    /// </summary>
    public BrowserHeartbeat? BestFor(string windowTitle, string? foregroundApp, TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        lock (_lock)
        {
            var fresh = _byInstance.Values.Where(v => v.At >= cutoff).ToList();
            var appBrowser = BrowserTokenOf(foregroundApp);
            if (appBrowser is not null)
            {
                var same = fresh.Where(v => string.Equals(v.Hb.Browser, appBrowser, StringComparison.OrdinalIgnoreCase)).ToList();
                if (same.Count > 0) fresh = same;
            }
            var titled = fresh
                .Where(v => v.Hb.Title.Length >= 3 &&
                            windowTitle.Contains(v.Hb.Title, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.Hb.Title.Length)
                .ThenByDescending(v => v.Hb.Focused) // tie pe titluri identice: instanța cu focus OS câștigă
                .ThenByDescending(v => v.At)
                .Select(v => v.Hb)
                .FirstOrDefault();
            if (titled is not null) return titled;
            return fresh
                .Where(v => v.Hb.Focused)
                .OrderByDescending(v => v.At)
                .Select(v => v.Hb)
                .FirstOrDefault();
        }
    }

    /// <summary>Extension reports only "edge" (UA has Edg/) or "chrome" (any other Chromium).</summary>
    private static string? BrowserTokenOf(string? app) => app?.ToLowerInvariant() switch
    {
        "msedge.exe" => "edge",
        null or "" => null,
        _ => app.Contains("chrome", StringComparison.OrdinalIgnoreCase) ? "chrome" : null,
    };

    /// <summary>Any fresh instance reporting an audible tab (for the video rule, decision #6).</summary>
    public bool AnyAudible(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        lock (_lock)
        {
            return _byInstance.Values.Any(v => v.At >= cutoff && (v.Hb.Audible || v.Hb.AnyAudible));
        }
    }
}
