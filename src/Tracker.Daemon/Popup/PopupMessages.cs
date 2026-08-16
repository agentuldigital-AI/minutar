namespace Tracker.Daemon.Popup;

/// <summary>What the picker knows about the moment it is asked for a line.</summary>
public sealed record PopupMessageContext(
    int Minutes,
    string Site,
    DateTimeOffset Now,
    int TimesToday,
    int TotalTodaySeconds,
    string? TopPriority);

/// <summary>
/// The warning popup used to show one fixed sentence. After the tenth time you stop reading
/// it and close it on reflex, so the text now rotates (user decision, 2026-08-04). Tone is
/// deliberately warm and factual — never accusing: a tracker that mocks you becomes an
/// opponent, and an opponent gets muted or uninstalled. The measured line (minutes on which
/// site) stays visible underneath, so the information never depends on the joke.
/// </summary>
public sealed class PopupMessagePicker
{
    private readonly Random _rng;
    private readonly Dictionary<string, List<int>> _unused = new(StringComparer.Ordinal);
    private string? _lastText;

    public PopupMessagePicker(int? seed = null) => _rng = seed is null ? new Random() : new Random(seed.Value);

    private static readonly Dictionary<string, string[]> Groups = new(StringComparer.Ordinal)
    {
        ["opening"] = new[]
        {
            "{minute} minute pe {site}. Doar țin socoteala.",
            "Ceasul merge. Tu decizi ce faci cu el.",
            "{minute} minute aici. Ziua e încă a ta.",
            "Pauza asta a devenit puțin mai lungă decât o pauză.",
        },
        ["long"] = new[]
        {
            "{minute} minute. Un episod întreg s-a dus.",
            "Jumătate de oră pe {site}. Cafeaua s-a răcit demult.",
            "{site} 1 – 0 tu. Mai joci repriza a doua?",
        },
        ["repeat"] = new[]
        {
            "Ne revedem. A {n}-a oară azi pe {site}.",
            "{site} și cu mine ne-am mai văzut azi. De {n} ori.",
            "Te cheamă, recunosc. {total} azi, în total.",
        },
        ["evening"] = new[]
        {
            "E {ora}. {site} e tot acolo și mâine.",
            "Târziu, și tot pe {site}. Zic și eu.",
            "Ziua aproape s-a încheiat. Asta e ultima ei bucată.",
        },
        ["morning"] = new[]
        {
            "Dimineața e cea mai ieftină oră a zilei. O dai pe {site}?",
            "Abia ai început ziua și ai și găsit {site}.",
        },
        ["priority"] = new[]
        {
            "Ți-ai propus «{prioritate}». Încă te așteaptă.",
            "Top 3 de azi are o bifă liberă.",
            "{prioritate} nu se face singură. {site}, în schimb, merge și fără tine.",
        },
    };

    public string Pick(PopupMessageContext ctx)
    {
        var eligible = EligibleGroups(ctx);
        // one retry with a different group when the draw repeats the previous line — better
        // than looping: with a single eligible group a repeat may be unavoidable
        var text = Draw(eligible[_rng.Next(eligible.Count)], ctx);
        if (text == _lastText && eligible.Count > 1)
            text = Draw(eligible[_rng.Next(eligible.Count)], ctx);
        _lastText = text;
        return text;
    }

    /// <summary>"opening" always qualifies, so there is never an empty set to draw from.</summary>
    private static List<string> EligibleGroups(PopupMessageContext ctx)
    {
        var groups = new List<string> { "opening" };
        if (ctx.Minutes >= 20) groups.Add("long");
        if (ctx.TimesToday >= 2) groups.Add("repeat");
        var hour = ctx.Now.Hour;
        if (hour >= 21 || hour < 5) groups.Add("evening");
        else if (hour < 11) groups.Add("morning");
        if (!string.IsNullOrWhiteSpace(ctx.TopPriority)) groups.Add("priority");
        return groups;
    }

    /// <summary>Draws without repetition: a group is only reshuffled once fully used.</summary>
    private string Draw(string group, PopupMessageContext ctx)
    {
        var pool = Groups[group];
        if (!_unused.TryGetValue(group, out var left) || left.Count == 0)
            _unused[group] = left = Enumerable.Range(0, pool.Length).ToList();
        var at = _rng.Next(left.Count);
        var idx = left[at];
        left.RemoveAt(at);
        return Fill(pool[idx], ctx);
    }

    private static string Fill(string template, PopupMessageContext ctx) => template
        .Replace("{minute}", ctx.Minutes.ToString())
        .Replace("{site}", ctx.Site)
        .Replace("{n}", ctx.TimesToday.ToString())
        .Replace("{ora}", ctx.Now.ToString("HH:mm"))
        .Replace("{total}", FormatDuration(ctx.TotalTodaySeconds))
        .Replace("{prioritate}", ctx.TopPriority ?? "");

    private static string FormatDuration(int seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}h {seconds % 3600 / 60}m" : $"{Math.Max(1, seconds / 60)} min";

    /// <summary>
    /// A readable site name for the message: "domain:youtube.com" reads as "YouTube", far
    /// better than the raw window title, which is long and full of separators. Falls back to
    /// the app name without its extension.
    /// </summary>
    public static string SiteNameOf(string? matchedRule, string app)
    {
        if (matchedRule is not null)
        {
            var colon = matchedRule.IndexOf(':');
            var value = colon >= 0 ? matchedRule[(colon + 1)..] : matchedRule;
            if (matchedRule.StartsWith("domain:", StringComparison.Ordinal))
            {
                var labels = value.Split('.');
                // skip generic prefixes so "www.exemplu.eu" reads "Exemplu", not "Www"
                var name = labels.FirstOrDefault(l =>
                    l.Length > 2 && !l.Equals("www", StringComparison.OrdinalIgnoreCase)) ?? value;
                return Capitalize(name);
            }
            if (value.Length > 0 && !matchedRule.StartsWith("app:", StringComparison.Ordinal))
                return Capitalize(value);
        }
        var bare = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
        return Capitalize(bare);
    }

    /// <summary>Plain capitalisation turns "youtube" into "Youtube", which reads as a typo in
    /// a message meant to sound human — the few sites that spell themselves differently get
    /// their real name.</summary>
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube"] = "YouTube",
        ["memebox"] = "MemeBox",
        ["tiktok"] = "TikTok",
        ["whatsapp"] = "WhatsApp",
        ["linkedin"] = "LinkedIn",
        ["github"] = "GitHub",
        ["chatgpt"] = "ChatGPT",
        ["reddit"] = "Reddit",
        ["twitch"] = "Twitch",
    };

    private static string Capitalize(string s) =>
        s.Length == 0 ? s
        : KnownNames.TryGetValue(s, out var known) ? known
        : char.ToUpperInvariant(s[0]) + s[1..];
}
