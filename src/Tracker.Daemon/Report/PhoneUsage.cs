using System.Text.Json;
using Tracker.Shared.Aw;
using Tracker.Shared.Config;
using Tracker.Shared.Storage;

namespace Tracker.Daemon.Report;

/// <summary>Un import: totalul unei perioade, plus aplicațiile din el.</summary>
public sealed record PhoneWeek(
    string Device, string From, string To, int TotalMinutes, int AvgDailyMinutes,
    int? Pickups, int? Notifications, string Source, string RecordedAt,
    IReadOnlyList<(string Name, int Minutes)> Apps);

/// <summary>
/// Citirea și agregarea timpului de pe telefon. Stă separat de <see cref="ReportService"/>
/// pentru că e alt fel de dată: nu e măsurată de noi, ci raportată de Apple, în totaluri pe
/// perioadă. De aceea NU intră niciodată în activeSeconds — ar amesteca secunde cronometrate
/// cu cifre rotunjite de altcineva. Raportul o expune într-un bloc propriu.
/// </summary>
public static class PhoneUsage
{
    /// <summary>
    /// Perioadele care ATING intervalul cerut. Nu se proratează pe zile: știm totalul
    /// perioadei, nu și când anume din ea s-a consumat, iar o împărțire la 7 ar inventa
    /// o precizie pe care datele nu o au.
    /// </summary>
    public static async Task<List<PhoneWeek>> ReadAsync(
        IEventStore store, string host, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        // citim mai larg decât intervalul: o perioadă începută înainte de `from` poate
        // ajunge în el, iar evenimentul e ancorat la ziua ei de început
        var events = await store.GetEventsRangeAsync(
            AwBuckets.PhoneUsage(host), from.AddDays(-40), to.AddDays(1), ct: ct);

        var weeks = new List<PhoneWeek>();
        foreach (var e in events)
        {
            if (e.Data.ValueKind != JsonValueKind.Object) continue;
            var fromStr = Str(e.Data, "from");
            var toStr = Str(e.Data, "to");
            if (!DateOnly.TryParse(fromStr, out var wFrom) || !DateOnly.TryParse(toStr, out var wTo)) continue;

            // Suprapunere cu intervalul cerut. AMBELE capete sunt EXCLUSIVE: „Jul 13–20"
            // înseamnă zilele 13..19 (7 bare în graficul Apple, media = total/7), iar `to`
            // al raportului e tot exclusiv (săptămâna 6–12 iul = [07-06, 07-13)). Cu
            // comparații inclusive, o perioadă atinsă doar la margine apărea și în
            // săptămâna dinainte, și în cea de după — aceleași 23h 20m în trei săptămâni.
            var wFromDt = wFrom.ToDateTime(TimeOnly.MinValue);
            var wToDt = wTo.ToDateTime(TimeOnly.MinValue);
            if (wFromDt >= to.Date || wToDt <= from.Date) continue;

            var apps = new List<(string, int)>();
            if (e.Data.TryGetProperty("apps", out var appsEl) && appsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in appsEl.EnumerateArray())
                {
                    var name = Str(a, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    apps.Add((name, Int(a, "minutes") ?? 0));
                }
            }

            weeks.Add(new PhoneWeek(
                Str(e.Data, "device") ?? "phone", fromStr!, toStr!,
                Int(e.Data, "totalMinutes") ?? 0, Int(e.Data, "avgDailyMinutes") ?? 0,
                Int(e.Data, "pickups"), Int(e.Data, "notifications"),
                Str(e.Data, "source") ?? "", Str(e.Data, "recordedAt") ?? "", apps));
        }

        // o corecție se scrie ca eveniment nou, nu înlocuiește nimic: la citire câștigă
        // ultima scriere pentru aceeași (device, perioadă)
        return weeks
            .GroupBy(w => (w.Device, w.From))
            .Select(g => g.OrderByDescending(w => w.RecordedAt, StringComparer.Ordinal).First())
            .OrderBy(w => w.From, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Cheia sub care se potrivește o aplicație cu regula ei. Screen Time afișează numele
    /// comercial complet („9GAG: Best LOL Pics &amp; GIFs"), iar de la un import la altul
    /// asistentul AI poate scrie când forma lungă, când cea scurtă — cu potrivire exactă,
    /// aceeași aplicație ar apărea o dată clasificată și o dată nu. Tăiem tot ce vine după
    /// „:" sau „ - ", ce rămâne e numele pe care îl recunoaște un om.
    /// </summary>
    public static string MatchKey(string name)
    {
        var n = name.Trim();
        var cut = n.IndexOf(':');
        if (cut > 0) n = n[..cut];
        foreach (var sep in new[] { " - ", " – ", " — " })
        {
            var i = n.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) n = n[..i];
        }
        return n.Trim();
    }

    /// <summary>
    /// Totalurile pe clase și proiecte, după regulile din <c>[[phone_apps]]</c>. Aplicațiile
    /// fără clasă NU se pun pe „neutru" — sunt numărate separat, ca utilizatorul să vadă cât
    /// din timp e încă neclasificat în loc să creadă că e neutru.
    /// </summary>
    public static object Summarize(IReadOnlyList<PhoneWeek> weeks, TrackerConfig cfg)
    {
        var rules = cfg.PhoneApps
            .Where(p => p.Name.Trim().Length > 0)
            .GroupBy(p => MatchKey(p.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var byClass = new Dictionary<string, int>(StringComparer.Ordinal);
        var byProject = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byApp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unclassified = 0;

        foreach (var w in weeks)
        {
            foreach (var (name, minutes) in w.Apps)
            {
                if (minutes <= 0) continue;
                // în clasament aplicația apare sub numele scurt: pe o lună care prinde două
                // importuri, „9GAG" și „9GAG: Best LOL Pics & GIFs" sunt aceeași aplicație și
                // ar fi ieșit două rânduri care se împart timpul între ele
                var key = MatchKey(name);
                byApp[key] = byApp.GetValueOrDefault(key) + minutes;
                if (rules.TryGetValue(key, out var rule))
                {
                    byClass[rule.Class] = byClass.GetValueOrDefault(rule.Class) + minutes;
                    if (rule.Project.Length > 0)
                        byProject[rule.Project] = byProject.GetValueOrDefault(rule.Project) + minutes;
                }
                else
                {
                    unclassified += minutes;
                }
            }
        }

        var total = weeks.Sum(w => w.TotalMinutes);
        var appsSum = byApp.Values.Sum();

        return new
        {
            totalMinutes = total,
            byClass,
            byProject = byProject.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
            unclassifiedMinutes = unclassified,
            // fiecare aplicație vine cu clasa și proiectul ei: pagina Telefon e un editor,
            // nu doar o listă, iar fără astea ar trebui să reconstruiască regulile din config
            // și să repete potrivirea pe MatchKey — o a doua implementare care ar diverge
            apps = byApp.OrderByDescending(kv => kv.Value).Select(kv =>
            {
                rules.TryGetValue(MatchKey(kv.Key), out var r);
                return new
                {
                    name = kv.Key,
                    minutes = kv.Value,
                    cls = r?.Class,
                    project = r is { Project.Length: > 0 } ? r.Project : null,
                };
            }),
            // Apple raportează un total mai mic decât suma aplicațiilor (site-urile se
            // numără și în browser, iar „Total Screen Time" se calculează altfel). Îl
            // expunem ca să poată fi arătat, nu ascuns ca o eroare de-a noastră.
            appsSumMinutes = appsSum,
            periods = weeks.Select(w => new
            {
                w.Device, w.From, w.To, w.TotalMinutes, w.AvgDailyMinutes,
                w.Pickups, w.Notifications, w.Source,
            }),
        };
    }

    private static string? Str(JsonElement data, string prop) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement data, string prop) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;
}
