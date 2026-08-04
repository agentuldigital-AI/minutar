using Tracker.Shared.Config;

namespace Tracker.Daemon.Engine;

/// <summary>
/// Project attribution (architecture §1.3, decisions #12 + TimeCamp model):
/// browser windows: profile match (extension label > AUMID fragment > Edge title fragment) BEATS keywords;
/// otherwise: longest whole-word keyword match on app+title+url; then a HoldSeconds grace
/// where the last matched project is kept (TimeCamp keeps ~20s).
/// </summary>
public sealed class AttributionEngine
{
    private string? _lastProject;
    private string? _lastApp;
    private string? _lastAumid;
    private DateTimeOffset _lastMatch;

    /// <summary>
    /// The stateless attribution rule — the ONE place that decides which project a window
    /// belongs to. The report used to carry its own copy (ReportService.ResolveProject) and
    /// the two drifted apart: the copy still matched a profile fragment as a substring of
    /// ANY browser title, so every window whose document was named after a client landed on
    /// that client's project, live and report disagreeing (2026-08-04). Both call this now.
    ///
    /// Precedence: explicit app/domain pins &gt; browser profile &gt; keywords.
    /// </summary>
    public static string? Resolve(
        TrackerConfig cfg, string app, string title, string aumid, string? url, string? profileLabel) =>
        MatchExplicit(cfg, app, title, url, IsBrowser(cfg, app))
        ?? MatchProfile(cfg, app, title, aumid, profileLabel)
        ?? MatchKeywords(cfg, app, title, url);

    public string? Attribute(
        TrackerConfig cfg, string app, string title, string aumid,
        string? url, string? profileLabel, DateTimeOffset now)
    {
        var project = Resolve(cfg, app, title, aumid, url, profileLabel);
        if (project is not null)
        {
            _lastProject = project;
            _lastApp = app;
            _lastAumid = aumid;
            _lastMatch = now;
            return project;
        }

        // TimeCamp-style hold, but ONLY within the same app: it bridges brief no-match
        // moments (tab switches, generic titles) — switching to ANOTHER app
        // (browser → Claude/WhatsApp) must drop the project instantly.
        //
        // For browsers "same app" is too coarse: every profile is the same chrome.exe, so
        // the hold carried a client's project across into a profile WITHOUT the extension
        // — switching from the Client A profile to the plain one kept showing her
        // project for 20 s (2026-08-04). Browser windows must also be the same profile,
        // identified by AUMID; a missing AUMID is not good enough proof to hold on.
        var sameWindowIdentity = !IsBrowser(cfg, app)
            || (aumid.Length > 0 && string.Equals(_lastAumid, aumid, StringComparison.OrdinalIgnoreCase));
        if (_lastProject is not null
            && app.Equals(_lastApp, StringComparison.OrdinalIgnoreCase)
            && sameWindowIdentity
            && (now - _lastMatch).TotalSeconds <= cfg.Attribution.HoldSeconds)
            return _lastProject;

        _lastProject = null;
        return null;
    }

    private static string? MatchExplicit(TrackerConfig cfg, string app, string title, string? url, bool isBrowser)
    {
        foreach (var p in cfg.Projects)
        {
            if (p.Apps.Any(a => a.Equals(app, StringComparison.OrdinalIgnoreCase)))
                return p.Name;
            // titleFallback only for browser windows — the same guard classification uses.
            // Without it a project domain leaked into EVERY app: "mail.zoho.eu" made any
            // window titled "Mail" (Outlook, explorer, anything) that client's project.
            if (p.Domains.Any(d => d.Length > 0
                    && ClassificationEngine.MatchesDomain(d, url, title, titleFallback: isBrowser)))
                return p.Name;
        }
        return null;
    }

    private static string? MatchProfile(TrackerConfig cfg, string app, string title, string aumid, string? label)
    {
        if (!IsBrowser(cfg, app)) return null;
        foreach (var p in cfg.Projects)
        {
            foreach (var frag in p.BrowserProfiles)
            {
                if (string.IsNullOrWhiteSpace(frag)) continue;
                if (label is not null && label.Contains(frag, StringComparison.OrdinalIgnoreCase)) return p.Name;
                if (aumid.Contains(frag, StringComparison.OrdinalIgnoreCase)) return p.Name;
                // Edge puts the profile display name in the window title (verified live
                // 2026-07-07) — but only in one specific place, so only that place counts.
                if (IsEdge(app) && EdgeTitleNamesProfile(title, frag)) return p.Name;
            }
        }
        return null;
    }

    private static string? MatchKeywords(TrackerConfig cfg, string app, string title, string? url)
    {
        var hay = $"{app} {title} {url}";
        string? best = null;
        var bestLen = 0;
        foreach (var p in cfg.Projects)
        {
            foreach (var kw in p.Keywords)
            {
                if (kw.Length > bestLen && ContainsWholeWord(hay, kw))
                {
                    best = p.Name;
                    bestLen = kw.Length;
                }
            }
        }
        return best;
    }

    public static bool IsBrowser(TrackerConfig cfg, string app) =>
        cfg.Browser.Processes.Contains(app, StringComparer.OrdinalIgnoreCase);

    private static bool IsEdge(string app) =>
        app.StartsWith("msedge", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Edge window titles end with " - &lt;profile&gt; - Microsoft Edge", so ONLY the
    /// last-but-one segment names the profile. Searching the whole title matched the page
    /// name instead: files called "CLIENTC_DECK_VANZARE" landed on the project whose profile
    /// is "Client C", no matter which profile had them open (2026-08-04). Note Edge writes a
    /// zero-width space inside "Microsoft Edge" — seen in real events, hence the strip.
    /// </summary>
    private static bool EdgeTitleNamesProfile(string title, string frag)
    {
        var parts = title.Split(" - ", StringSplitOptions.None);
        if (parts.Length < 3) return false;
        // strip zero-width/bidi marks: Edge really does write one inside "Microsoft Edge"
        var tail = string.Concat(parts[^1].Where(c =>
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.Format)).Trim();
        if (!tail.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase)) return false;
        return parts[^2].Contains(frag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whole-word match: neighbours must not be letters/digits (keywords may contain '-').</summary>
    public static bool ContainsWholeWord(string hay, string word)
    {
        if (word.Length == 0) return false;
        var idx = 0;
        while ((idx = hay.IndexOf(word, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = idx == 0 || !char.IsLetterOrDigit(hay[idx - 1]);
            var end = idx + word.Length;
            var after = end >= hay.Length || !char.IsLetterOrDigit(hay[end]);
            if (before && after) return true;
            idx++;
        }
        return false;
    }
}
