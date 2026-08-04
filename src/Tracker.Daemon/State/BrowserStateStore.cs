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
    private readonly Dictionary<string, HashSet<string>> _aumidsByInstance = new();

    private static string KeyOf(string? browser, string? profile) => $"{browser}|{profile}";

    public void Update(BrowserHeartbeat hb)
    {
        lock (_lock)
        {
            _byInstance[KeyOf(hb.Browser, hb.Profile)] = (hb, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Records that this window AppUserModelID belongs to an instance, learned from a
    /// CONFIRMED match (the foreground window title matched that instance's tab). Chrome
    /// gives each profile its own AUMID for taskbar grouping — "Chrome.UserData.Profile1"
    /// vs "Chrome.UserData.Profile2" — which is the only native signal that tells two
    /// profiles of the same browser apart. Edge reports a single "MSEdge" for all profiles,
    /// so nothing is learned there and nothing changes.
    /// </summary>
    public void LearnAumid(string? browser, string? profile, string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return;
        lock (_lock)
        {
            var key = KeyOf(browser, profile);
            if (!_aumidsByInstance.TryGetValue(key, out var set))
                _aumidsByInstance[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(aumid);
        }
    }

    /// <summary>
    /// True only when this AUMID is KNOWN to belong to a DIFFERENT profile of the same
    /// browser — i.e. we have positive proof the foreground window is not this instance's.
    /// An unseen AUMID returns false, so nothing regresses while the mapping is still being
    /// learned, or on browsers that do not vary it per profile.
    /// </summary>
    public bool AumidBelongsToOtherProfile(string? browser, string? profile, string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return false;
        var key = KeyOf(browser, profile);
        var prefix = $"{browser}|";
        lock (_lock)
        {
            if (_aumidsByInstance.TryGetValue(key, out var own) && own.Contains(aumid)) return false;
            foreach (var (k, set) in _aumidsByInstance)
            {
                if (k == key || !k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (set.Contains(aumid)) return true;
            }
            return false;
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

            // No tab matches the foreground window. With a real window title that is the
            // signature of a browser profile WITHOUT the extension — falling back to "the
            // freshest instance that claims focus" then hands that window another profile's
            // identity: switching from the client profile to a plain one kept showing
            // "Client B · Productiv" over a Calendly tab (2026-08-04). The stale claim can
            // survive up to maxAge, because a blur report that never lands (asleep service
            // worker, dropped request) leaves Focused=true behind.
            //
            // Unknown window ⇒ no browser state. It classifies on its title instead, which
            // is honest: we genuinely do not know what that profile has open.
            if (windowTitle.Length > 0) return null;

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
