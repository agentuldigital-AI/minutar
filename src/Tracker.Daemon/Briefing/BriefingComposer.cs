using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tracker.Daemon.Briefing;

/// <summary>Cifrele dintr-o zi, extrase din raport — atât cât încape într-un mesaj de telefon.</summary>
public sealed record BriefingData(
    DateOnly Day,
    double ActiveSeconds,
    double ProductiveSeconds,
    double NeutralSeconds,
    double UnproductiveSeconds,
    double PhoneMinutes,
    IReadOnlyList<(string Name, double Seconds)> TopApps,
    double WeekAverageSeconds);

/// <summary>
/// Textul briefingului de dimineață. Separat de trimitere ca să poată fi testat: compunerea e
/// pură (cifre → text), iar rețeaua stă în <see cref="TelegramClient"/>.
///
/// Raportul vine ca tip anonim din <c>ReportService.BuildAsync</c>, deci nu are membri accesibili
/// din afară — îl citim prin JSON. Toate câmpurile sunt tratate ca opționale: un raport fără
/// telefon sau fără aplicații produce un mesaj mai scurt, nu o excepție.
/// </summary>
public static class BriefingComposer
{
    private static readonly CultureInfo Ro = CultureInfo.GetCultureInfo("ro-RO");

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        // raportul e deja camelCase, dar normalizăm ca să nu depindem de forma tipului anonim
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Serializează raportul (tip anonim) și extrage doar ce intră în briefing.</summary>
    public static BriefingData FromReport(DateOnly day, object dayReport, object? weekReport, int weekDays = 7)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dayReport, ReportJson));
        var root = doc.RootElement;

        var totals = Obj(root, "totals");
        var byClass = totals is { } t ? Obj(t, "byClass") : null;

        var apps = new List<(string, double)>();
        if (root.TryGetProperty("byApp", out var byApp) && byApp.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in byApp.EnumerateArray().Take(3))
            {
                var name = Str(a, "name");
                var sec = Num(a, "seconds");
                if (name.Length > 0 && sec > 0) apps.Add((name, sec));
            }
        }

        var phone = 0d;
        if (root.TryGetProperty("phone", out var ph) && ph.ValueKind == JsonValueKind.Object)
            phone = Num(ph, "totalMinutes");

        var weekAvg = 0d;
        if (weekReport is not null && weekDays > 0)
        {
            using var wdoc = JsonDocument.Parse(JsonSerializer.Serialize(weekReport, ReportJson));
            var wt = Obj(wdoc.RootElement, "totals");
            if (wt is { }) weekAvg = Num(wt.Value, "activeSeconds") / weekDays;
        }

        return new BriefingData(
            day,
            totals is { } ? Num(totals.Value, "activeSeconds") : 0,
            byClass is { } ? Num(byClass.Value, "productive") : 0,
            byClass is { } ? Num(byClass.Value, "neutral") : 0,
            byClass is { } ? Num(byClass.Value, "unproductive") : 0,
            phone,
            apps,
            weekAvg);
    }

    /// <summary>Mesajul propriu-zis. HTML (parse_mode), cu tot ce vine din date escapat.</summary>
    public static string Compose(BriefingData d)
    {
        var sb = new StringBuilder();
        sb.Append("<b>Ieri, ").Append(Esc(d.Day.ToString("dddd d MMMM", Ro))).Append("</b>");

        var nimicPePc = d.ActiveSeconds < 60;
        var nimicPeTelefon = d.PhoneMinutes < 1;

        if (nimicPePc && nimicPeTelefon)
        {
            sb.Append("\n\nN-am înregistrat nimic ieri — nici pe calculator, nici pe telefon.");
            return sb.ToString();
        }

        if (nimicPePc)
        {
            sb.Append("\n\nPe calculator, nimic înregistrat ieri.");
        }
        else
        {
            sb.Append("\n\nActiv <b>").Append(Dur(d.ActiveSeconds)).Append("</b>");
            if (d.WeekAverageSeconds >= 60)
                sb.Append(" — media ultimelor 7 zile: ").Append(Dur(d.WeekAverageSeconds));

            var clase = new List<string>();
            Add(clase, "Productiv", d.ProductiveSeconds, d.ActiveSeconds);
            Add(clase, "Neutru", d.NeutralSeconds, d.ActiveSeconds);
            Add(clase, "Neproductiv", d.UnproductiveSeconds, d.ActiveSeconds);
            if (clase.Count > 0) sb.Append('\n').Append(string.Join(" · ", clase));
        }

        if (!nimicPeTelefon)
            sb.Append("\nTelefon ").Append(Dur(d.PhoneMinutes * 60));

        if (d.TopApps.Count > 0)
            sb.Append("\n\nTop: ")
              .Append(string.Join(" · ", d.TopApps.Select(a => $"{Esc(a.Name)} {Dur(a.Seconds)}")));

        return sb.ToString();

        static void Add(List<string> into, string label, double seconds, double total)
        {
            if (seconds < 60) return;
            var pct = total > 0 ? (int)Math.Round(seconds / total * 100) : 0;
            into.Add($"{label} {Dur(seconds)} ({pct}%)");
        }
    }

    /// <summary>Format scurt, cum se citește pe telefon: „{h}h {mm}m", iar sub o oră doar „{m}m".</summary>
    public static string Dur(double seconds)
    {
        var total = (int)Math.Round(seconds / 60);
        var h = total / 60;
        var m = total % 60;
        return h > 0 ? $"{h}h {m:00}m" : $"{m}m";
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static JsonElement? Obj(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? e : null;

    private static double Num(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;

    private static string Str(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
}
