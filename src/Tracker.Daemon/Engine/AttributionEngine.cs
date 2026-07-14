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
    private DateTimeOffset _lastMatch;

    public string? Attribute(
        TrackerConfig cfg, string app, string title, string aumid,
        string? url, string? profileLabel, DateTimeOffset now)
    {
        // precedence: explicit app/domain pins > browser profile > keywords
        var project = MatchExplicit(cfg, app, title, url)
                      ?? MatchProfile(cfg, app, title, aumid, profileLabel)
                      ?? MatchKeywords(cfg, app, title, url);
        if (project is not null)
        {
            _lastProject = project;
            _lastApp = app;
            _lastMatch = now;
            return project;
        }

        // TimeCamp-style hold, but ONLY within the same app: it bridges brief no-match
        // moments (tab switches, generic titles) — switching to ANOTHER app
        // (browser → Claude/WhatsApp) must drop the project instantly
        if (_lastProject is not null
            && app.Equals(_lastApp, StringComparison.OrdinalIgnoreCase)
            && (now - _lastMatch).TotalSeconds <= cfg.Attribution.HoldSeconds)
            return _lastProject;

        _lastProject = null;
        return null;
    }

    private static string? MatchExplicit(TrackerConfig cfg, string app, string title, string? url)
    {
        foreach (var p in cfg.Projects)
        {
            if (p.Apps.Any(a => a.Equals(app, StringComparison.OrdinalIgnoreCase)))
                return p.Name;
            if (p.Domains.Any(d => d.Length > 0 && ClassificationEngine.MatchesDomain(d, url, title)))
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
                // Edge puts the profile display name in the window title (verified live 2026-07-07)
                if (title.Contains(frag, StringComparison.OrdinalIgnoreCase)) return p.Name;
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
