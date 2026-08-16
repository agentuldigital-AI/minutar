using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tracker.Daemon.Briefing;

/// <summary>Ce fel de interval rezumă mesajul. Schimbă doar antetul și eticheta comparației.</summary>
public enum BriefingPeriod
{
    Day,
    Week,
    Month,
    /// <summary>Trimis când tocmai ai importat date de telefon, pentru exact perioada importată.</summary>
    PhoneImport,
}

/// <summary>Cifrele unui interval, atât cât încape într-un mesaj de telefon.</summary>
public sealed record BriefingData(
    BriefingPeriod Kind,
    DateOnly From,
    /// <summary>Exclusiv, ca peste tot în rapoarte: [From, To).</summary>
    DateOnly To,
    double ActiveSeconds,
    double ProductiveSeconds,
    double NeutralSeconds,
    double UnproductiveSeconds,
    double PhoneMinutes,
    IReadOnlyList<(string Name, double Seconds)> TopApps,
    /// <summary>Media zilnică anterioară (zi) sau totalul perioadei dinainte (săptămână/lună). 0 = se omite.</summary>
    double CompareSeconds = 0,
    /// <summary>Nu există date de telefon pentru interval — se cere un import, nu se tace.</summary>
    bool PhoneMissing = false);

/// <summary>
/// Textul briefingului. Separat de trimitere ca să poată fi testat: compunerea e pură
/// (cifre → text), iar rețeaua stă în <see cref="TelegramClient"/>.
///
/// Raportul vine ca tip anonim din <c>ReportService.BuildAsync</c>, deci nu are membri
/// accesibili din afară — îl citim prin JSON. Toate câmpurile sunt tratate ca opționale: un
/// raport fără telefon sau fără aplicații produce un mesaj mai scurt, nu o excepție.
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
    public static BriefingData FromReport(
        BriefingPeriod kind, DateOnly from, DateOnly to,
        object report, object? compareReport = null, int compareDivisor = 1)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(report, ReportJson));
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
        var phoneMissing = true;
        if (root.TryGetProperty("phone", out var ph) && ph.ValueKind == JsonValueKind.Object)
        {
            phone = Num(ph, "totalMinutes");
            // „lipsă" înseamnă că nu există NICIUN import care atinge intervalul, nu că e zero
            phoneMissing = !(ph.TryGetProperty("periods", out var per)
                             && per.ValueKind == JsonValueKind.Array
                             && per.GetArrayLength() > 0);
        }

        var compare = 0d;
        if (compareReport is not null && compareDivisor > 0)
        {
            using var cdoc = JsonDocument.Parse(JsonSerializer.Serialize(compareReport, ReportJson));
            var ct = Obj(cdoc.RootElement, "totals");
            if (ct is { }) compare = Num(ct.Value, "activeSeconds") / compareDivisor;
        }

        return new BriefingData(
            kind, from, to,
            totals is { } ? Num(totals.Value, "activeSeconds") : 0,
            byClass is { } ? Num(byClass.Value, "productive") : 0,
            byClass is { } ? Num(byClass.Value, "neutral") : 0,
            byClass is { } ? Num(byClass.Value, "unproductive") : 0,
            phone, apps, compare, phoneMissing);
    }

    /// <summary>Mesajul propriu-zis. HTML (parse_mode), cu tot ce vine din date escapat.</summary>
    public static string Compose(BriefingData d)
    {
        var sb = new StringBuilder();
        sb.Append("<b>").Append(Esc(Header(d))).Append("</b>");

        var nimicPePc = d.ActiveSeconds < 60;
        var nimicPeTelefon = d.PhoneMinutes < 1;

        if (nimicPePc && nimicPeTelefon)
        {
            sb.Append("\n\n").Append(d.Kind == BriefingPeriod.Day
                ? "N-am înregistrat nimic ieri — nici pe calculator, nici pe telefon."
                : "N-am înregistrat nimic în perioada asta — nici pe calculator, nici pe telefon.");
            return sb.ToString() + PhoneHint(d);
        }

        if (nimicPePc)
        {
            sb.Append("\n\nPe calculator, nimic înregistrat.");
        }
        else
        {
            sb.Append("\n\nActiv <b>").Append(Dur(d.ActiveSeconds)).Append("</b>");
            if (d.CompareSeconds >= 60)
                sb.Append(" — ").Append(CompareLabel(d.Kind)).Append(": ").Append(Dur(d.CompareSeconds));

            var clase = new List<string>();
            Add(clase, "Productiv", d.ProductiveSeconds, d.ActiveSeconds);
            Add(clase, "Neutru", d.NeutralSeconds, d.ActiveSeconds);
            Add(clase, "Neproductiv", d.UnproductiveSeconds, d.ActiveSeconds);
            if (clase.Count > 0) sb.Append('\n').Append(string.Join(" · ", clase));
        }

        if (!nimicPeTelefon)
        {
            sb.Append("\nTelefon ").Append(Dur(d.PhoneMinutes * 60));
            if (!nimicPePc)
                sb.Append(" · împreună <b>").Append(Dur(d.ActiveSeconds + d.PhoneMinutes * 60)).Append("</b>");
        }

        if (d.TopApps.Count > 0)
            sb.Append("\n\nTop: ")
              .Append(string.Join(" · ", d.TopApps.Select(a => $"{Esc(a.Name)} {Dur(a.Seconds)}")));

        return sb.ToString() + PhoneHint(d);

        static void Add(List<string> into, string label, double seconds, double total)
        {
            if (seconds < 60) return;
            var pct = total > 0 ? (int)Math.Round(seconds / total * 100) : 0;
            into.Add($"{label} {Dur(seconds)} ({pct}%)");
        }
    }

    /// <summary>
    /// Pe săptămână și pe lună, tăcerea ar fi înșelătoare: ai crede că n-ai stat pe telefon,
    /// când de fapt n-ai importat încă. Zilnicul nu cere nimic — Screen Time vine pe săptămâni.
    /// </summary>
    private static string PhoneHint(BriefingData d) =>
        d.PhoneMissing && d.Kind is BriefingPeriod.Week or BriefingPeriod.Month
            ? "\n\n<i>Telefonul lipsește din interval — importă Screen Time din pagina Telefon și îți trimit totalul.</i>"
            : "";

    private static string Header(BriefingData d)
    {
        var last = d.To.AddDays(-1); // To e exclusiv; ultima zi din interval
        return d.Kind switch
        {
            BriefingPeriod.Day => $"Ieri, {d.From.ToString("dddd d MMMM", Ro)}",
            BriefingPeriod.Week => $"Săptămâna trecută, {Span(d.From, last)}",
            BriefingPeriod.Month => $"Luna trecută, {d.From.ToString("MMMM yyyy", Ro)}",
            _ => $"Telefon importat, {Span(d.From, last)}",
        };
    }

    /// <summary>„10–16 august", iar peste graniță de lună „28 iulie – 3 august".</summary>
    private static string Span(DateOnly a, DateOnly b) =>
        a.Month == b.Month
            ? $"{a.Day}–{b.Day} {a.ToString("MMMM", Ro)}"
            : $"{a.ToString("d MMMM", Ro)} – {b.ToString("d MMMM", Ro)}";

    private static string CompareLabel(BriefingPeriod k) => k switch
    {
        BriefingPeriod.Day => "media ultimelor 7 zile",
        BriefingPeriod.Week => "săptămâna dinainte",
        BriefingPeriod.Month => "luna dinainte",
        _ => "perioada dinainte",
    };

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
