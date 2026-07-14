using Tracker.Shared.Config;

namespace Tracker.Daemon.Engine;

public sealed record Classification(string Class, string? MatchedRule, bool ExceptionApplied);

/// <summary>
/// productive / neutral / unproductive classification (architecture §1.3, decision #8).
/// Rule precedence: app > domain > title; within a type, config order. A YouTube exception
/// (title keyword / channel whitelist) flips an unproductive match to productive; manual
/// popup marks live as ordinary [classification] rules in tracker.toml.
/// </summary>
public sealed class ClassificationEngine
{
    public Classification Classify(
        TrackerConfig cfg,
        string app, string title, string? url, string? channel)
    {
        var result = MatchRules(cfg, app, title, url);
        if (result.Class == "unproductive" && IsException(cfg, title, url, channel))
            return result with { Class = "productive", ExceptionApplied = true };
        return result;
    }

    private static Classification MatchRules(TrackerConfig cfg, string app, string title, string? url)
    {
        // for browsers the DOMAIN decides first (an "msedge.exe → productive" app rule must
        // not whitewash youtube.com); for everything else the app rule is the most specific
        var isBrowser = cfg.Browser.Processes.Contains(app, StringComparer.OrdinalIgnoreCase);
        var order = isBrowser
            ? new[] { "domain", "title", "app" }
            : new[] { "app", "domain", "title" };
        foreach (var type in order)
        {
            foreach (var rule in cfg.Classification.Rules)
            {
                if (rule.Match != type) continue;
                if (Matches(rule, app, title, url, isBrowser))
                    return new Classification(rule.Class, $"{rule.Match}:{rule.Value}", false);
            }
        }
        return new Classification(cfg.Classification.Default, null, false);
    }

    private static bool Matches(ClassificationRule rule, string app, string title, string? url, bool isBrowser) => rule.Match switch
    {
        "app" => string.Equals(app, rule.Value, StringComparison.OrdinalIgnoreCase),
        "domain" => MatchesDomain(rule.Value, url, title, titleFallback: isBrowser),
        "title" => ContainsKeyword(title, rule.Value),
        _ => false,
    };

    /// <summary>
    /// With a URL (extension data, M5): exact host or subdomain match. Without one
    /// (pre-M5 / extension down): fall back to matching the site name (domain minus TLD)
    /// as a whole word in the title — "youtube.com" matches "… - YouTube …".
    /// titleFallback=false disables that fallback: a NON-browser window ("Zoom.exe" cu
    /// „zoom" în titlu) nu are voie să prindă reguli/atribuiri de DOMENIU prin titlu.
    /// </summary>
    public static bool MatchesDomain(string domain, string? url, string title, bool titleFallback = true)
    {
        if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var host = u.Host;
            return host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                   || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        }
        if (!titleFallback) return false;
        var site = domain.Split('.')[0];
        return site.Length >= 4 && AttributionEngine.ContainsWholeWord(title, site);
    }

    private static bool IsException(TrackerConfig cfg, string title, string? url, string? channel)
    {
        var isYoutube = (url?.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ?? false)
                        || title.Contains(" - YouTube", StringComparison.OrdinalIgnoreCase);
        if (!isYoutube) return false;
        foreach (var kw in cfg.YoutubeExceptions.TitleKeywords)
            if (ContainsKeyword(title, kw))
                return true;
        return channel is not null &&
               cfg.YoutubeExceptions.Channels.Any(c => c.Equals(channel, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Keyword match for title rules / YouTube exceptions. Keywords saved by older
    /// popup builds end in a display "…" the live title never contains — match on the real
    /// prefix instead; and never let an empty value become a match-everything catch-all.</summary>
    private static bool ContainsKeyword(string title, string keyword)
    {
        var k = keyword.TrimEnd('…');
        return k.Length > 0 && title.Contains(k, StringComparison.OrdinalIgnoreCase);
    }
}
