using System.Text;
using System.Text.RegularExpressions;
using Tracker.Shared.Config;

namespace Tracker.Daemon.Calendar;

/// <summary>Ce fel de intrare e evenimentul — de asta depinde cu ce are voie să fie comparat.</summary>
public enum CalendarKind
{
    /// <summary>Apel video. Singurul fel care se compară cu ce a măsurat tracker-ul.</summary>
    Online,

    /// <summary>Programare la o adresă fizică: timp blocat, dar nu în fața calculatorului.</summary>
    InPerson,

    /// <summary>Memento personal, fără loc și fără apel.</summary>
    Block,
}

/// <summary>O intrare din calendar, redusă la ce ne trebuie.</summary>
public sealed record CalendarEvent(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    CalendarKind Kind,
    bool AllDay = false,
    /// <summary>
    /// Id-ul de la Google. Când muți un eveniment, id-ul RĂMÂNE — asta face urmărirea
    /// amânărilor posibilă fără să ghicim după titlu.
    /// </summary>
    string Id = "",
    /// <summary>
    /// Face parte dintr-o serie care se repetă. Fără distincția asta, o ședință săptămânală
    /// ar părea amânată în fiecare săptămână — și ai învăța să ignori avertismentul.
    /// </summary>
    bool Recurring = false)
{
    public double Seconds => Math.Max(0, (End - Start).TotalSeconds);
}

/// <summary>
/// Împărțirea evenimentelor pe feluri. Pură și publică pentru același motiv ca
/// <c>ReportService.DetectMeetings</c>: e singura parte cu reguli, deci trebuie testabilă
/// fără rețea și fără cheie.
/// </summary>
public static class CalendarClassifier
{
    /// <summary>
    /// Ordinea contează, și nu e cea evidentă.
    ///
    /// Un link video decide primul, pentru că e cel mai sigur semnal. Vine însă din câmpul
    /// LOCAȚIE, nu din lista de invitați: uneltele de rezervare pun acolo linkul de apel și nu
    /// adaugă niciun invitat, așa că o integrare care se ia după participanți ratează exact
    /// ședințele de lucru și le prinde doar pe cele trimise manual.
    ///
    /// O adresă fizică vine imediat după și bate invitații și titlul: dacă te duci undeva, nu
    /// ești la calculator, deci tracker-ul n-are ce măsura. Fără regula asta, orice programare
    /// la un cabinet ar apărea în comparație ca „ședință planificată care nu s-a întâmplat".
    ///
    /// Un URL care nu e gazdă cunoscută nu decide nimic: poate fi la fel de bine o hartă. Cade
    /// mai departe pe invitați și titlu, iar dacă nici acolo nu e semnal, rămâne memento.
    /// </summary>
    public static CalendarKind Classify(
        string? title, string? location, bool hasConference, int otherAttendees, CalendarConfig cfg)
    {
        if (hasConference) return CalendarKind.Online;

        var loc = (location ?? "").Trim();
        var host = HostOf(loc);

        if (host is not null && cfg.MeetingHosts.Any(h => HostMatches(host, h)))
            return CalendarKind.Online;

        if (loc.Length > 0 && host is null) return CalendarKind.InPerson;

        if (otherAttendees > 0) return CalendarKind.Online;

        var t = Fold(title ?? "");
        if (cfg.MeetingTitleKeywords.Any(k => ContainsWord(t, Fold(k)))) return CalendarKind.Online;

        return CalendarKind.Block;
    }

    private static readonly Regex UrlRx =
        new(@"(?:https?://|\bwww\.)\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EmailRx =
        new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);

    /// <summary>
    /// Numere de telefon, cu prefix explicit („+", „00", „0"). Forma e strictă dinadins: un
    /// tipar lax ar mânca datele din titluri („2026-08-17") și ar strica nume care n-au nimic.
    /// </summary>
    private static readonly Regex PhoneRx =
        new(@"(?:\+|00|\b0)\d[\d\s().-]{7,}\d", RegexOptions.Compiled);

    private static readonly Regex SpacesRx = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Scoate din titlu ce n-are ce căuta într-un mesaj care pleacă de pe calculator: linkuri
    /// (cu tot cu id-uri de ședință și parole), adrese de email, numere de telefon.
    ///
    /// Adresa și linkul evenimentului nu trec oricum de parsare — se citesc doar ca să afle ce
    /// fel de eveniment e, și se aruncă. Titlul era singura cale prin care mai putea ieși ceva,
    /// dacă îl scrii chiar acolo.
    ///
    /// Ce NU poate face: o adresă poștală scrisă în titlu arată exact ca un nume obișnuit
    /// („Strada Cuiva 10" nu se deosebește automat de „Studio Cuiva"). Pentru cazul ăla există
    /// comutatorul agenda_in_briefing, care oprește titlurile cu totul.
    /// </summary>
    public static string CleanTitle(string? title)
    {
        var s = UrlRx.Replace(title ?? "", " ");
        s = EmailRx.Replace(s, " ");
        s = PhoneRx.Replace(s, " ");
        s = SpacesRx.Replace(s, " ").Trim();
        // separatori rămași orfani după ce s-a scos ce urma după ei: „Apel —" → „Apel"
        return s.Trim(' ', '-', '–', '—', '|', ',', ':', ';', '(', ')', '[', ']', '/', '·', '.');
    }

    /// <summary>
    /// Gazda primului link din text, fără „www.". Restul URL-ului conține id-ul ședinței și nu
    /// se citește niciodată — nici aici, nici în log.
    /// </summary>
    public static string? HostOf(string text)
    {
        var i = text.IndexOf("http", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        var rest = text[i..];
        var slashes = rest.IndexOf("//", StringComparison.Ordinal);
        if (slashes < 0) return null;

        var host = rest[(slashes + 2)..];
        var end = host.IndexOfAny([' ', '/', '?', '#', ',', ')', '\n', '\r', '\t']);
        if (end >= 0) host = host[..end];

        host = host.Trim().ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return host.Length > 0 && host.Contains('.') ? host : null;
    }

    /// <summary>„us06web.zoom.us" se potrivește cu „zoom.us"; „notzoom.us" nu.</summary>
    private static bool HostMatches(string host, string pattern)
    {
        var p = pattern.Trim().ToLowerInvariant();
        if (p.StartsWith("www.", StringComparison.Ordinal)) p = p[4..];
        return p.Length > 0
               && (host.Equals(p, StringComparison.Ordinal)
                   || host.EndsWith("." + p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Potrivire pe cuvânt întreg: „call" n-are voie să se aprindă în „Callisto". Ambele
    /// argumente sunt deja trecute prin <see cref="Fold"/>.
    /// </summary>
    private static bool ContainsWord(string haystack, string needle)
    {
        if (needle.Length == 0) return false;
        for (var i = 0; (i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0; i++)
        {
            var before = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
            var j = i + needle.Length;
            var after = j >= haystack.Length || !char.IsLetterOrDigit(haystack[j]);
            if (before && after) return true;
        }
        return false;
    }

    /// <summary>
    /// Timpul ACOPERIT de o mulțime de evenimente: reuniunea intervalelor, nu suma lor. Două
    /// intrări suprapuse blochează o oră, nu două. Contează mai mult decât pare — peste un bloc
    /// lung de tip „plecat din oraș" cad și ședințele zilei, iar adunarea simplă ar raporta mai
    /// multe ore blocate decât are ziua.
    /// </summary>
    public static double UnionSeconds(IEnumerable<CalendarEvent> events)
    {
        double total = 0;
        DateTimeOffset start = default, end = default;
        var open = false;

        foreach (var e in events.Where(e => e.End > e.Start).OrderBy(e => e.Start))
        {
            if (!open) { start = e.Start; end = e.End; open = true; }
            else if (e.Start > end) { total += (end - start).TotalSeconds; start = e.Start; end = e.End; }
            else if (e.End > end) { end = e.End; }
        }
        if (open) total += (end - start).TotalSeconds;
        return total;
    }

    /// <summary>Diacriticele nu au voie să conteze: „Ședință" și „sedinta" sunt același cuvânt.</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
        {
            sb.Append(ch switch
            {
                'ă' or 'â' or 'à' or 'á' or 'ä' => 'a',
                'î' or 'í' or 'ì' => 'i',
                'ș' or 'ş' => 's',
                'ț' or 'ţ' => 't',
                'é' or 'è' or 'ê' => 'e',
                'ó' or 'ö' or 'ô' => 'o',
                'ú' or 'ü' or 'û' => 'u',
                _ => ch,
            });
        }
        return sb.ToString();
    }
}
