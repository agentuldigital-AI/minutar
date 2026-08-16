using System.Globalization;
using System.Text;
using System.Text.Json;
// Importat cu numele scurte dinadins: „Calendar" simplu ar fi ambiguu între namespace-ul
// Tracker.Daemon.Calendar și clasa System.Globalization.Calendar, folosită mai jos.
using Tracker.Daemon.Calendar;

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

/// <summary>O activitate din clasament: aplicație sau site, cu timpul ei în secunde.</summary>
public sealed record TopItem(string Name, double Seconds);

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
    IReadOnlyList<TopItem>? TopProductive = null,
    IReadOnlyList<TopItem>? TopUnproductive = null,
    /// <summary>Timp în ședințe video — etichetă peste timpul de calculator, nu în plus față de el.</summary>
    double MeetingSeconds = 0,
    int MeetingCount = 0,
    double PhoneProductiveMinutes = 0,
    double PhoneNeutralMinutes = 0,
    double PhoneUnproductiveMinutes = 0,
    /// <summary>Diferența dintre totalul Apple și suma aplicațiilor numite — vezi pagina Telefon.</summary>
    double PhoneUnclassifiedMinutes = 0,
    IReadOnlyList<TopItem>? PhoneTopProductive = null,
    IReadOnlyList<TopItem>? PhoneTopUnproductive = null,
    /// <summary>Media zilnică anterioară (zi) sau totalul perioadei dinainte (săptămână/lună). 0 = se omite.</summary>
    double CompareSeconds = 0,
    /// <summary>Nu există date de telefon pentru interval — se cere un import, nu se tace.</summary>
    bool PhoneMissing = false,
    /// <summary>
    /// Ce ai AZI în calendar. Singura parte a mesajului care privește înainte, nu înapoi —
    /// de-aia stă la coadă și cu antet propriu. Doar pe briefingul zilnic.
    /// </summary>
    IReadOnlyList<CalendarEvent>? Agenda = null,
    /// <summary>
    /// Dacă titlurile din agendă se scriu ca atare. Ele conțin nume de clienți și adrese, iar
    /// mesajul pleacă spre Telegram — pe false rămân orele și felul, fără cine și unde.
    /// </summary>
    bool AgendaTitles = true,
    /// <summary>Cât apel video era PLANIFICAT în calendar, față de cât s-a măsurat efectiv.</summary>
    double PlannedMeetingSeconds = 0,
    int PlannedMeetingCount = 0,
    /// <summary>Programări la o adresă: explică timpul planificat pe care calculatorul n-avea cum să-l vadă.</summary>
    int PlannedInPersonCount = 0,
    /// <summary>Evenimente pe care le tot împingi mai încolo. Doar pe briefingul zilnic.</summary>
    IReadOnlyList<RescheduleAlert>? Postponed = null);

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
    private const int TopCount = 3;

    /// <summary>Mai multe de-atât nu mai sunt un îndemn, sunt o listă de făcut.</summary>
    private const int MaxPostponed = 5;

    private static readonly CultureInfo Ro = CultureInfo.GetCultureInfo("ro-RO");

    /// <summary>
    /// Un browser e RECIPIENT, nu activitate: timpul lui e deja detaliat de site-urile din el,
    /// care apar separat în aceeași listă. Aceeași regulă ca la telefon — dacă l-am număra,
    /// clasamentul ar arăta „msedge.exe" acolo unde răspunsul util e site-ul propriu-zis.
    /// </summary>
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "opera.exe", "opera_gx.exe",
        "vivaldi.exe", "arc.exe", "librewolf.exe", "waterfox.exe",
    };

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

        var phone = 0d;
        var phoneMissing = true;
        double phProd = 0, phNeu = 0, phUnp = 0, phUncl = 0;
        List<TopItem> phTopProd = new(), phTopUnp = new();
        if (root.TryGetProperty("phone", out var ph) && ph.ValueKind == JsonValueKind.Object)
        {
            phone = Num(ph, "totalMinutes");
            // „lipsă" înseamnă că nu există NICIUN import care atinge intervalul, nu că e zero
            phoneMissing = !(ph.TryGetProperty("periods", out var per)
                             && per.ValueKind == JsonValueKind.Array
                             && per.GetArrayLength() > 0);
            phUncl = Num(ph, "unclassifiedMinutes");
            if (Obj(ph, "byClass") is { } pbc)
            {
                phProd = Num(pbc, "productive");
                phNeu = Num(pbc, "neutral");
                phUnp = Num(pbc, "unproductive");
            }
            phTopProd = PhoneTop(ph, "productive");
            phTopUnp = PhoneTop(ph, "unproductive");
        }

        double meetSec = 0;
        var meetCount = 0;
        if (root.TryGetProperty("meetings", out var mt) && mt.ValueKind == JsonValueKind.Object)
        {
            meetSec = Num(mt, "seconds");
            meetCount = (int)Num(mt, "count");
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
            phone,
            PcTop(root, "productive"), PcTop(root, "unproductive"), meetSec, meetCount,
            phProd, phNeu, phUnp, phUncl, phTopProd, phTopUnp,
            compare, phoneMissing);
    }

    /// <summary>Aplicațiile și site-urile unei clase, amestecate și sortate — browserele sărite.</summary>
    private static List<TopItem> PcTop(JsonElement root, string cls)
    {
        var outp = new List<TopItem>();
        if (Obj(root, "classDetail") is not { } cd || Obj(cd, cls) is not { } node) return outp;

        foreach (var (prop, skipBrowsers) in new[] { ("apps", true), ("domains", false) })
        {
            if (!node.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in arr.EnumerateArray())
            {
                var name = Str(e, "name");
                var sec = Num(e, "seconds");
                if (name.Length == 0 || sec < 60) continue;
                if (skipBrowsers && Browsers.Contains(name)) continue;
                outp.Add(new TopItem(name, sec));
            }
        }
        return outp.OrderByDescending(i => i.Seconds).Take(TopCount).ToList();
    }

    /// <summary>Aplicațiile de telefon dintr-o clasă. Browserele sunt deja excluse de PhoneUsage.</summary>
    private static List<TopItem> PhoneTop(JsonElement phone, string cls)
    {
        var outp = new List<TopItem>();
        if (!phone.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array) return outp;
        foreach (var a in apps.EnumerateArray())
        {
            if (!string.Equals(Str(a, "cls"), cls, StringComparison.OrdinalIgnoreCase)) continue;
            var name = Str(a, "name");
            var min = Num(a, "minutes");
            if (name.Length > 0 && min >= 1) outp.Add(new TopItem(name, min * 60));
        }
        return outp.OrderByDescending(i => i.Seconds).Take(TopCount).ToList();
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
            // Agenda rămâne: o zi fără nimic măsurat, dar cu ședințe azi, e exact ziua în care
            // mesajul trebuie să-ți spună ce urmează.
            return sb.ToString() + Agenda(d) + Postponed(d) + PhoneHint(d);
        }

        // Totalul pe ambele dispozitive vine PRIMUL: e cifra care răspunde la „cât am stat în
        // fața unui ecran", iar la coada mesajului stătea exact acolo unde nu se citește. Apare
        // doar când există date de pe ambele — altfel „calculator + telefon" ar minți despre ce
        // însumează.
        if (!nimicPePc && !nimicPeTelefon)
            sb.Append("\n\n<b>Timp total ").Append(Dur(d.ActiveSeconds + d.PhoneMinutes * 60))
              .Append("</b>\n<i>calculator + telefon</i>");

        // Un bloc per dispozitiv, cu clasele UNA PE RÂND. Pe un ecran de telefon, trei perechi
        // cifră-procent înșirate pe același rând se citesc greu; pe rânduri separate se compară
        // dintr-o privire.
        if (nimicPePc)
        {
            sb.Append("\n\nPe calculator, nimic înregistrat.");
        }
        else
        {
            sb.Append("\n\n<b>Calculator ").Append(Dur(d.ActiveSeconds)).Append("</b>");
            if (d.CompareSeconds >= 60)
                sb.Append("\n<i>").Append(CompareLabel(d.Kind)).Append(": ").Append(Dur(d.CompareSeconds)).Append("</i>");

            Line(sb, "Productiv", d.ProductiveSeconds, d.ActiveSeconds);
            Line(sb, "Neutru", d.NeutralSeconds, d.ActiveSeconds);
            Line(sb, "Neproductiv", d.UnproductiveSeconds, d.ActiveSeconds);

            Meetings(sb, d);

            Top(sb, "Top activități productive", d.TopProductive);
            Top(sb, "Top activități neproductive", d.TopUnproductive);
        }

        if (!nimicPeTelefon)
        {
            var tot = d.PhoneMinutes;
            sb.Append("\n\n<b>Telefon ").Append(Dur(tot * 60)).Append("</b>");

            Line(sb, "Productiv", d.PhoneProductiveMinutes * 60, tot * 60);
            Line(sb, "Neutru", d.PhoneNeutralMinutes * 60, tot * 60);
            Line(sb, "Neproductiv", d.PhoneUnproductiveMinutes * 60, tot * 60);

            // Golul dintre totalul Apple și suma aplicațiilor numite. Fără el, procentele de mai
            // sus par să nu se închidă și ai crede că lipsește ceva din raport.
            var gol = d.PhoneUnclassifiedMinutes
                      + Math.Max(0, tot - (d.PhoneProductiveMinutes + d.PhoneNeutralMinutes
                                           + d.PhoneUnproductiveMinutes + d.PhoneUnclassifiedMinutes));
            Line(sb, "Nedetaliat", gol * 60, tot * 60);

            Top(sb, "Top activități productive", d.PhoneTopProductive);
            Top(sb, "Top activități neproductive", d.PhoneTopUnproductive);
        }

        return sb.ToString() + Agenda(d) + Postponed(d) + PhoneHint(d);

        // Cât s-a măsurat, față de cât era planificat. Pe zi, linia „ai avut ședințe" era o
        // informație pe care o aveai oricum — de-aia lipsea. Cu planul din calendar alături
        // devine altceva: „cât din ce ți-ai propus s-a și întâmplat".
        static void Meetings(StringBuilder sb, BriefingData d)
        {
            var masurat = d.MeetingSeconds >= 60;
            var planificat = d.PlannedMeetingSeconds >= 60;

            if (d.Kind is BriefingPeriod.Day && !planificat) return;
            if (!masurat && !planificat) return;

            if (!masurat)
            {
                sb.Append("\nȘedințe — nimic măsurat, deși aveai ")
                  .Append(Dur(d.PlannedMeetingSeconds)).Append(" planificate în calendar");
            }
            else
            {
                sb.Append("\nȘedințe ").Append(Dur(d.MeetingSeconds))
                  .Append(" (").Append(d.MeetingCount).Append(d.MeetingCount == 1 ? " ședință)" : " ședințe)");
                if (d.ActiveSeconds > 0)
                    sb.Append(" · ").Append((int)Math.Round(d.MeetingSeconds / d.ActiveSeconds * 100))
                      .Append("% din timpul pe calculator");

                if (planificat)
                {
                    sb.Append("\nPlanificat în calendar ").Append(Dur(d.PlannedMeetingSeconds));
                    // Sub cinci minute nu e o diferență, e rotunjire — iar o diferență raportată
                    // acolo unde nu există te învață să nu mai crezi cifra.
                    var delta = d.MeetingSeconds - d.PlannedMeetingSeconds;
                    if (Math.Abs(delta) >= 300)
                        sb.Append(delta > 0 ? $" · {Dur(delta)} peste plan" : $" · {Dur(-delta)} nu s-au regăsit");
                }
            }

            // Explică de ce o parte din timpul planificat n-are corespondent măsurat: dacă te
            // duci undeva, nu ești la calculator. Fără rândul ăsta, ar arăta a ședințe ratate.
            if (d.PlannedInPersonCount > 0)
                sb.Append("\n<i>Plus ").Append(d.PlannedInPersonCount)
                  .Append(d.PlannedInPersonCount == 1 ? " programare la o adresă" : " programări la adrese")
                  .Append(" — n-au cum să apară pe calculator.</i>");
        }

        static void Line(StringBuilder sb, string label, double seconds, double total)
        {
            if (seconds < 60) return;
            var pct = total > 0 ? (int)Math.Round(seconds / total * 100) : 0;
            sb.Append('\n').Append(label).Append(' ').Append(Dur(seconds)).Append(" (").Append(pct).Append("%)");
        }

        static void Top(StringBuilder sb, string label, IReadOnlyList<TopItem>? items)
        {
            if (items is null || items.Count == 0) return;
            sb.Append("\n\n<b>").Append(label).Append("</b>\n")
              .Append(string.Join("\n", items.Select(i => $"· {Esc(i.Name)} {Dur(i.Seconds)}")));
        }
    }

    /// <summary>
    /// Singura parte a mesajului care privește înainte, nu înapoi. De-aia stă la coadă și cu
    /// antet propriu: restul briefingului spune „ieri", iar amestecul le-ar face neclare pe
    /// amândouă. Doar pe briefingul zilnic — o agendă pe lună ar fi o listă, nu o informație.
    /// </summary>
    private static string Agenda(BriefingData d)
    {
        if (d.Kind != BriefingPeriod.Day || d.Agenda is null || d.Agenda.Count == 0) return "";

        var apeluri = d.Agenda.Count(e => e.Kind == CalendarKind.Online);
        var adrese = d.Agenda.Count(e => e.Kind == CalendarKind.InPerson);
        // „Blocat" înseamnă că ai un angajament, nu că ai scris o notă. Un memento întins peste
        // toată ziua — cum sunt cele de tip „plecat din oraș" — ar raporta paisprezece ore
        // blocate lângă două apeluri de câte-o oră: aritmetic corect, ca informație fals.
        var blocat = CalendarClassifier.UnionSeconds(
            d.Agenda.Where(e => !e.AllDay && e.Kind != CalendarKind.Block));

        var sb = new StringBuilder("\n\n<b>Azi în calendar</b>");

        var sumar = new List<string>();
        if (apeluri > 0) sumar.Add(apeluri == 1 ? "1 apel video" : $"{apeluri} apeluri video");
        if (adrese > 0) sumar.Add(adrese == 1 ? "1 programare la o adresă" : $"{adrese} programări la adrese");
        if (blocat >= 60) sumar.Add($"{Dur(blocat)} blocate");
        if (sumar.Count > 0) sb.Append('\n').Append(string.Join(" · ", sumar));

        foreach (var e in d.Agenda)
        {
            sb.Append("\n· ").Append(e.AllDay
                ? "toată ziua"
                : $"{e.Start.LocalDateTime:HH:mm}–{e.End.LocalDateTime:HH:mm}");
            sb.Append(' ').Append(d.AgendaTitles ? Esc(e.Title) : Fel(e.Kind));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Ce tot împingi mai încolo. Stă după agendă, pentru că e singura parte a mesajului care
    /// cere ceva de la tine — restul doar raportează.
    ///
    /// Titlurile respectă același comutator ca agenda: fără ele rămâne numărul de mutări, care
    /// e oricum informația care contează.
    /// </summary>
    private static string Postponed(BriefingData d)
    {
        if (d.Kind != BriefingPeriod.Day || d.Postponed is null || d.Postponed.Count == 0) return "";

        var sb = new StringBuilder("\n\n<b>Tot amâni</b>");
        foreach (var a in d.Postponed.Take(MaxPostponed))
        {
            sb.Append("\n· ")
              .Append(d.AgendaTitles ? Esc(a.Title) : "un eveniment")
              .Append(" — mutat de ").Append(Ori(a.Moves))
              .Append(", acum pe ").Append(a.Start.ToString("d MMMM", Ro));
        }
        sb.Append(d.Postponed.Count == 1
            ? "\n<i>Nu vrei să-l faci?</i>"
            : "\n<i>Nu vrei să le faci?</i>");
        return sb.ToString();
    }

    /// <summary>„de 3 ori" se citește prost în românește pentru numerele mici.</summary>
    private static string Ori(int n) => n switch
    {
        2 => "două ori",
        3 => "trei ori",
        4 => "patru ori",
        5 => "cinci ori",
        _ => $"{n} ori",
    };

    /// <summary>Când titlurile sunt oprite, rămâne felul: ora și ce fel de lucru e, fără cine.</summary>
    private static string Fel(CalendarKind k) => k switch
    {
        CalendarKind.Online => "apel video",
        CalendarKind.InPerson => "programare la o adresă",
        _ => "rezervat",
    };

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
