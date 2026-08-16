using Tracker.Daemon.Briefing;
using Tracker.Daemon.Calendar;
using Tracker.Shared.Config;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// Regulile prin care un eveniment din calendar devine (sau nu) „ședință".
///
/// Miezul, și partea contraintuitivă: semnalul stă în câmpul LOCAȚIE, nu în lista de invitați.
/// Uneltele de rezervare pun linkul de apel în locație și nu adaugă niciun invitat, așa că o
/// integrare care se ia după participanți — cum face orice exemplu din documentație — ratează
/// exact ședințele de lucru și le prinde doar pe cele trimise manual.
///
/// A doua regulă la fel de importantă: o adresă fizică bate orice alt semnal. Dacă te duci
/// undeva, nu ești la calculator, deci tracker-ul n-are ce măsura. Fără asta, o programare la
/// cabinet ar apărea în comparație ca ședință planificată care nu s-a întâmplat.
///
/// Toate datele de aici sunt inventate.
/// </summary>
public class CalendarClassifierTests
{
    private static CalendarConfig Cfg() => new()
    {
        MeetingHosts = new List<string> { "zoom.us", "meet.google.com", "whereby.com" },
        MeetingTitleKeywords = new List<string> { "call", "meeting", "ședință", "apel" },
    };

    private static CalendarKind Clasa(string? titlu, string? locatie = null,
                                      bool conferinta = false, int invitati = 0) =>
        CalendarClassifier.Classify(titlu, locatie, conferinta, invitati, Cfg());

    [Fact]
    public void LinkVideoInLocatie_EsteApel()
    {
        // cazul care se pierde dacă te iei după invitați: rezervare automată, zero participanți
        Assert.Equal(CalendarKind.Online,
            Clasa("Discuție de 30 de minute", "https://zoom.us/j/0000000000?pwd=xxxx"));
    }

    [Fact]
    public void SubdomeniuDeGazdaCunoscuta_EsteTotApel()
    {
        Assert.Equal(CalendarKind.Online, Clasa("Ceva", "https://us06web.zoom.us/j/0000000000"));
    }

    [Fact]
    public void GazdaDoarAsemanatoare_NuEsteApel()
    {
        // „nuzoom.us" nu e subdomeniu de „zoom.us"; potrivirea e pe etichetă, nu pe sufix de text
        Assert.NotEqual(CalendarKind.Online, Clasa("Ceva", "https://nuzoom.us/altceva"));
    }

    [Fact]
    public void AdresaFizica_EsteProgramareNuApel()
    {
        Assert.Equal(CalendarKind.InPerson, Clasa("Control", "Strada Inventată 10, Localitate"));
    }

    [Fact]
    public void AdresaFizicaCuInvitati_RamaneProgramare()
    {
        // dacă te duci undeva, nu ești la calculator — indiferent câți oameni vin
        Assert.Equal(CalendarKind.InPerson,
            Clasa("Întâlnire", "Strada Inventată 10, Localitate", invitati: 3));
    }

    [Fact]
    public void ConferintaGoogle_EsteApel()
    {
        Assert.Equal(CalendarKind.Online, Clasa("Fără locație", conferinta: true));
    }

    [Fact]
    public void DoarInvitati_EsteApel()
    {
        Assert.Equal(CalendarKind.Online, Clasa("Fără locație", invitati: 2));
    }

    [Fact]
    public void DoarTitlu_EsteApel()
    {
        // plasa pentru ședințele trecute în calendar din cap: nici link, nici invitați
        Assert.Equal(CalendarKind.Online, Clasa("Apel cu echipa"));
    }

    [Fact]
    public void DiacriticeleNuConteaza()
    {
        Assert.Equal(CalendarKind.Online, Clasa("Sedinta de proiect"));
        Assert.Equal(CalendarKind.Online, Clasa("Ședință de proiect"));
    }

    [Fact]
    public void CuvantulCheieNuSeAprindeInAltCuvant()
    {
        // „call" în „Callisto" nu e o ședință — potrivirea e pe cuvânt întreg
        Assert.Equal(CalendarKind.Block, Clasa("Callisto"));
    }

    [Fact]
    public void MementoSimplu_NuEsteNimic()
    {
        Assert.Equal(CalendarKind.Block, Clasa("Plata chiriei"));
    }

    [Fact]
    public void LinkCareNuEGazdaDeApel_NuDecideSingur()
    {
        // un link de hartă e tot un URL; fără alt semnal rămâne memento, nu devine apel
        Assert.Equal(CalendarKind.Block, Clasa("Undeva", "https://maps.example.com/?q=ceva"));
    }

    [Theory]
    // linkul de apel scris chiar în titlu — cu tot cu id de ședință și parolă
    [InlineData("Apel echipă https://zoom.us/j/12345?pwd=secret", "Apel echipă")]
    [InlineData("https://meet.google.com/abc-defg-hij", "")]
    [InlineData("Discuție — www.exemplu-inventat.ro/pagina", "Discuție")]
    // adrese de email
    [InlineData("Sincronizare cu cineva@example.com", "Sincronizare cu")]
    // numere de telefon, cu prefix explicit
    [InlineData("Sună la +40 722 000 111", "Sună la")]
    [InlineData("Programare 0722000111", "Programare")]
    // ce NU are voie să se piardă: datele și orele arată a numere, dar nu sunt
    [InlineData("Retrospectivă 2026-08-17", "Retrospectivă 2026-08-17")]
    [InlineData("Sprint 2026", "Sprint 2026")]
    [InlineData("Nume Client SRL", "Nume Client SRL")]
    public void CleanTitle_ScoateDoarCeTrebuie(string intrare, string asteptat)
    {
        Assert.Equal(asteptat, CalendarClassifier.CleanTitle(intrare));
    }

    [Fact]
    public void CleanTitle_NuAruncaPeNimic()
    {
        Assert.Equal("", CalendarClassifier.CleanTitle(null));
        Assert.Equal("", CalendarClassifier.CleanTitle("   "));
    }

    [Fact]
    public void TitlulCuratNuSchimbaClasificarea()
    {
        // clasificarea se uită la titlul brut: un titlu format doar din link rămâne apel,
        // chiar dacă după curățare nu mai are ce afișa
        Assert.Equal(CalendarKind.Online, Clasa("Ceva", "https://zoom.us/j/1"));
        Assert.Equal("", CalendarClassifier.CleanTitle("https://zoom.us/j/1"));
    }

    [Fact]
    public void HostOf_PastreazaDoarGazda()
    {
        // restul URL-ului conține id-ul ședinței și n-are voie să iasă de aici
        Assert.Equal("zoom.us", CalendarClassifier.HostOf("https://zoom.us/j/123456?pwd=secret"));
        Assert.Equal("zoom.us", CalendarClassifier.HostOf("Alătură-te: https://www.zoom.us/j/1 acum"));
        Assert.Null(CalendarClassifier.HostOf("Strada Inventată 10"));
    }
}

/// <summary>
/// Timpul acoperit de o mulțime de evenimente e REUNIUNEA intervalelor, nu suma lor. Contează
/// mai mult decât pare: peste un bloc lung de tip „plecat din oraș" cad și ședințele zilei, iar
/// adunarea simplă ar raporta mai multe ore blocate decât are ziua.
/// </summary>
public class CalendarUnionTests
{
    private static DateTimeOffset At(int h, int m = 0)
    {
        var dt = new DateTime(2026, 8, 17, h, m, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
    }

    private static CalendarEvent Ev(int h1, int m1, int h2, int m2) =>
        new("x", At(h1, m1), At(h2, m2), CalendarKind.Online);

    [Fact]
    public void IntervaleSeparate_SeAduna()
    {
        var t = CalendarClassifier.UnionSeconds([Ev(9, 0, 10, 0), Ev(11, 0, 11, 30)]);
        Assert.Equal(90 * 60, t);
    }

    [Fact]
    public void IntervaleSuprapuse_SeNumaraOSingura()
    {
        var t = CalendarClassifier.UnionSeconds([Ev(9, 0, 10, 0), Ev(9, 30, 10, 30)]);
        Assert.Equal(90 * 60, t);
    }

    [Fact]
    public void IntervalInghititDeAltul_NuAdaugaNimic()
    {
        var t = CalendarClassifier.UnionSeconds([Ev(9, 0, 12, 0), Ev(10, 0, 11, 0)]);
        Assert.Equal(180 * 60, t);
    }

    [Fact]
    public void ListaGoala_EsteZero()
    {
        Assert.Equal(0, CalendarClassifier.UnionSeconds([]));
    }
}

/// <summary>
/// „Ai mutat asta de trei ori."
///
/// Regula fără de care ar fi zgomot: seriile recurente NU se pun la socoteală. O ședință
/// săptămânală se mută legitim în fiecare săptămână, iar raportată ca amânare ar apărea de
/// cincizeci de ori pe an — exact felul de alarmă care te învață s-o ignori.
/// </summary>
public class RescheduleTrackerTests
{
    private const int Prag = 3;

    private static DateOnly Zi(int d) => new(2026, 8, d);

    private static CalendarEvent Ev(string id, int ziua, string titlu = "Ceva inventat", bool serie = false)
    {
        var dt = new DateTime(2026, 8, ziua, 10, 0, 0, DateTimeKind.Unspecified);
        var start = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
        return new CalendarEvent(titlu, start, start.AddHours(1), CalendarKind.Block, Id: id, Recurring: serie);
    }

    /// <summary>Rulează observațiile una după alta, ca zilele reale.</summary>
    private static (Dictionary<string, RescheduleEntry> State, List<RescheduleAlert> Last) Rula(
        params (int Azi, CalendarEvent[] Vazute)[] pasi)
    {
        var stare = new Dictionary<string, RescheduleEntry>();
        var ultimele = new List<RescheduleAlert>();
        foreach (var (azi, vazute) in pasi)
            (stare, ultimele) = RescheduleTracker.Observe(stare, vazute, Zi(azi), Prag);
        return (stare, ultimele);
    }

    [Fact]
    public void PrimaIntalnire_NuEsteOMutare()
    {
        var (stare, alerte) = Rula((1, [Ev("a", 5)]));
        Assert.Empty(alerte);
        Assert.Equal(0, stare["a"].Moves);
    }

    [Fact]
    public void MutatDeTreiOriInainte_TeAnunta()
    {
        // exact cazul: azi pe mâine, mâine pe poimâine, poimâine pe răspoimâine
        var (_, alerte) = Rula(
            (1, [Ev("a", 5)]), (2, [Ev("a", 6)]), (3, [Ev("a", 7)]), (4, [Ev("a", 8)]));

        var a = Assert.Single(alerte);
        Assert.Equal(3, a.Moves);
        Assert.Equal(Zi(8), a.Start);
    }

    [Fact]
    public void SubPrag_Tace()
    {
        var (_, alerte) = Rula((1, [Ev("a", 5)]), (2, [Ev("a", 6)]), (3, [Ev("a", 7)]));
        Assert.Empty(alerte);
    }

    [Fact]
    public void SeriaRecurenta_NuEsteNiciodataOAmanare()
    {
        // ședința săptămânală: se mută în fiecare săptămână, și e normal
        var (stare, alerte) = Rula(
            (1, [Ev("s", 5, serie: true)]), (2, [Ev("s", 12, serie: true)]),
            (3, [Ev("s", 19, serie: true)]), (4, [Ev("s", 26, serie: true)]));

        Assert.Empty(alerte);
        Assert.Empty(stare);
    }

    [Fact]
    public void MutatMaiDevreme_NuEsteAmanare()
    {
        var (stare, alerte) = Rula((1, [Ev("a", 20)]), (2, [Ev("a", 18)]), (3, [Ev("a", 15)]));
        Assert.Empty(alerte);
        Assert.Equal(0, stare["a"].Moves);
    }

    [Fact]
    public void MutatInAceeasiZi_NuEsteAmanare()
    {
        // ajustare de oră, nu fugă de sarcină
        var dt1 = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Unspecified);
        var dt2 = new DateTime(2026, 8, 20, 17, 0, 0, DateTimeKind.Unspecified);
        CalendarEvent La(DateTime d) => new("x", new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)),
            new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)).AddHours(1),
            CalendarKind.Block, Id: "a");

        var (stare, _) = Rula((1, [La(dt1)]), (2, [La(dt2)]));
        Assert.Equal(0, stare["a"].Moves);
    }

    [Fact]
    public void AcelasiPrag_NuSeRepeta()
    {
        // fără regula asta, un eveniment amânat de trei ori ar fi pomenit în fiecare zi
        var stare = new Dictionary<string, RescheduleEntry>();
        List<RescheduleAlert> alerte;
        foreach (var (azi, ziua) in new[] { (1, 5), (2, 6), (3, 7), (4, 8) })
            (stare, alerte) = RescheduleTracker.Observe(stare, [Ev("a", ziua)], Zi(azi), Prag);

        // a patra observație a dat alerta; a cincea, cu evenimentul nemișcat, tace
        (stare, alerte) = RescheduleTracker.Observe(stare, [Ev("a", 8)], Zi(5), Prag);
        Assert.Empty(alerte);

        // dar o mutare NOUĂ, peste prag, se anunță din nou
        (_, alerte) = RescheduleTracker.Observe(stare, [Ev("a", 9)], Zi(6), Prag);
        Assert.Equal(4, Assert.Single(alerte).Moves);
    }

    [Fact]
    public void EvenimentFaraId_EsteIgnorat()
    {
        var fara = new CalendarEvent("x", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1), CalendarKind.Block);
        var (stare, alerte) = RescheduleTracker.Observe(new Dictionary<string, RescheduleEntry>(),
            [fara], Zi(1), Prag);

        Assert.Empty(stare);
        Assert.Empty(alerte);
    }

    [Fact]
    public void StareaPrimitaNuEsteModificata()
    {
        // funcția e pură: cine o cheamă nu trebuie să se trezească cu starea lui schimbată
        var initiala = new Dictionary<string, RescheduleEntry>
        {
            ["a"] = new() { Start = "2026-08-05", Moves = 1, Seen = "2026-08-01" },
        };
        RescheduleTracker.Observe(initiala, [Ev("a", 9)], Zi(2), Prag);

        Assert.Equal(1, initiala["a"].Moves);
        Assert.Equal("2026-08-05", initiala["a"].Start);
    }

    [Fact]
    public void EvenimenteleVechiSeUita()
    {
        // ceva nevăzut de două luni: șters sau ieșit din fereastră, oricum nu ne mai interesează
        var initiala = new Dictionary<string, RescheduleEntry>
        {
            ["vechi"] = new() { Start = "2026-01-01", Moves = 5, Seen = "2026-01-01" },
        };
        var (stare, _) = RescheduleTracker.Observe(initiala, [], Zi(1), Prag);
        Assert.Empty(stare);
    }
}

/// <summary>
/// Cum arată calendarul în mesaj: agenda de azi și comparația dintre cât era planificat și cât
/// s-a măsurat. Comparația e răspunsul la o întrebare reală — „de ce apar mai puține minute de
/// apel decât știu că am avut" — deci trebuie să spună și cazul în care nu s-a măsurat nimic.
/// </summary>
public class BriefingCalendarTests
{
    private static DateTimeOffset At(int h, int m = 0)
    {
        var dt = new DateTime(2026, 8, 17, h, m, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
    }

    private static BriefingData Data(
        BriefingPeriod kind = BriefingPeriod.Day,
        double active = 6 * 3600,
        double meeting = 0, int meetingCount = 0,
        IReadOnlyList<CalendarEvent>? agenda = null, bool titluri = true,
        double planned = 0, int plannedCount = 0, int inPerson = 0,
        IReadOnlyList<RescheduleAlert>? amanate = null) =>
        new(kind, new DateOnly(2026, 8, 16), new DateOnly(2026, 8, 17),
            active, active * 0.6, active * 0.3, active * 0.1, 0,
            MeetingSeconds: meeting, MeetingCount: meetingCount,
            Agenda: agenda, AgendaTitles: titluri,
            PlannedMeetingSeconds: planned, PlannedMeetingCount: plannedCount,
            PlannedInPersonCount: inPerson, Postponed: amanate);

    [Fact]
    public void AmanateleAparDoarPeZi()
    {
        var a = new List<RescheduleAlert> { new("Sarcină inventată", 3, new DateOnly(2026, 8, 20)) };

        var zi = BriefingComposer.Compose(Data(amanate: a));
        Assert.Contains("Tot amâni", zi);
        Assert.Contains("Sarcină inventată", zi);
        Assert.Contains("mutat de trei ori", zi);
        Assert.Contains("20 august", zi);

        Assert.DoesNotContain("Tot amâni",
            BriefingComposer.Compose(Data(BriefingPeriod.Week, amanate: a)));
    }

    [Fact]
    public void AmanateleRespectaComutatorulDeTitluri()
    {
        var a = new List<RescheduleAlert> { new("Nume De Client SRL", 4, new DateOnly(2026, 8, 20)) };
        var text = BriefingComposer.Compose(Data(amanate: a, titluri: false));

        Assert.DoesNotContain("Nume De Client", text);
        Assert.Contains("mutat de patru ori", text);
    }

    [Fact]
    public void AgendaApareDoarPeZi()
    {
        var agenda = new List<CalendarEvent> { new("Apel inventat", At(9), At(9, 30), CalendarKind.Online) };

        Assert.Contains("Azi în calendar", BriefingComposer.Compose(Data(agenda: agenda)));
        Assert.DoesNotContain("Azi în calendar",
            BriefingComposer.Compose(Data(BriefingPeriod.Week, agenda: agenda)));
    }

    [Fact]
    public void AgendaScrieOraSiTitlul()
    {
        var agenda = new List<CalendarEvent> { new("Apel inventat", At(9), At(9, 30), CalendarKind.Online) };
        var text = BriefingComposer.Compose(Data(agenda: agenda));

        Assert.Contains("09:00–09:30", text);
        Assert.Contains("Apel inventat", text);
    }

    [Fact]
    public void TitluriOprite_NuScapaNiciUnNume()
    {
        // garanția de confidențialitate: pe agenda_in_briefing = false, spre Telegram pleacă
        // ora și felul, niciodată numele clientului sau adresa
        var agenda = new List<CalendarEvent>
        {
            new("Nume De Client SRL", At(9), At(9, 30), CalendarKind.Online),
            new("Control la o adresă", At(11), At(12), CalendarKind.InPerson),
        };
        var text = BriefingComposer.Compose(Data(agenda: agenda, titluri: false));

        Assert.DoesNotContain("Nume De Client", text);
        Assert.DoesNotContain("Control la o adresă", text);
        Assert.Contains("09:00–09:30", text);
        Assert.Contains("apel video", text);
    }

    [Fact]
    public void MementoulLungNuUmflaTimpulBlocat()
    {
        // de ce există regula: o notă întinsă peste toată ziua, lângă un apel scurt. Adunate,
        // ar raporta aproape o zi întreagă „blocată" — aritmetic corect, ca informație fals.
        var agenda = new List<CalendarEvent>
        {
            new("Notă lungă inventată", At(10), At(23, 59), CalendarKind.Block),
            new("Apel inventat", At(12), At(12, 30), CalendarKind.Online),
        };
        var text = BriefingComposer.Compose(Data(agenda: agenda));

        Assert.Contains("30m blocate", text);
        Assert.DoesNotContain("13h", text);
        // memento-ul rămâne în listă — doar nu mai umflă sumarul
        Assert.Contains("Notă lungă inventată", text);
    }

    [Fact]
    public void EvenimentPeZiIntreaga_NuAreOra()
    {
        var agenda = new List<CalendarEvent>
        {
            new("Ceva inventat", At(0), At(0).AddDays(1), CalendarKind.Block, AllDay: true),
        };
        Assert.Contains("toată ziua", BriefingComposer.Compose(Data(agenda: agenda)));
    }

    [Fact]
    public void PeZi_FaraPlanificat_NuScrieDespreSedinte()
    {
        // comportamentul dinainte: pe zi știai oricum că ai avut o ședință
        var text = BriefingComposer.Compose(Data(meeting: 40 * 60, meetingCount: 1));
        Assert.DoesNotContain("Ședințe", text);
    }

    [Fact]
    public void PeZi_CuPlanificat_CompataCeleDoua()
    {
        var text = BriefingComposer.Compose(
            Data(meeting: 40 * 60, meetingCount: 1, planned: 60 * 60, plannedCount: 1));

        Assert.Contains("Ședințe 40m", text);
        Assert.Contains("Planificat în calendar 1h 00m", text);
        Assert.Contains("20m nu s-au regăsit", text);
    }

    [Fact]
    public void MasuratPestePlan_SpuneAsta()
    {
        var text = BriefingComposer.Compose(
            Data(meeting: 90 * 60, meetingCount: 2, planned: 60 * 60, plannedCount: 1));
        Assert.Contains("30m peste plan", text);
    }

    [Fact]
    public void DiferentaSubCinciMinute_NuSeRaporteaza()
    {
        // sub cinci minute nu e o diferență, e rotunjire — raportată, te învață să nu crezi cifra
        var text = BriefingComposer.Compose(
            Data(meeting: 62 * 60, meetingCount: 1, planned: 60 * 60, plannedCount: 1));

        Assert.Contains("Planificat în calendar", text);
        Assert.DoesNotContain("peste plan", text);
        Assert.DoesNotContain("nu s-au regăsit", text);
    }

    [Fact]
    public void PlanificatDarNimicMasurat_ORaporteazaExplicit()
    {
        // exact întrebarea de la care a pornit tot: aveam apeluri, de ce nu apar minutele?
        var text = BriefingComposer.Compose(Data(planned: 2 * 3600, plannedCount: 2));
        Assert.Contains("nimic măsurat", text);
        Assert.Contains("2h 00m planificate", text);
    }

    [Fact]
    public void ProgramarileLaAdresa_ExplicaDiferenta()
    {
        var text = BriefingComposer.Compose(
            Data(meeting: 30 * 60, meetingCount: 1, planned: 30 * 60, plannedCount: 1, inPerson: 2));
        Assert.Contains("2 programări la adrese", text);
    }

    [Fact]
    public void FaraCalendar_MesajulRamaneCaInainte()
    {
        // calendarul oprit sau picat n-are voie să schimbe nimic din ce era deja acolo
        var text = BriefingComposer.Compose(Data(BriefingPeriod.Week, meeting: 3 * 3600, meetingCount: 4));

        Assert.Contains("Ședințe 3h 00m", text);
        Assert.DoesNotContain("Planificat în calendar", text);
        Assert.DoesNotContain("Azi în calendar", text);
    }

    [Fact]
    public void ZiFaraNimicMasurat_TotArataAgenda()
    {
        // ziua în care mesajul chiar trebuie să spună ce urmează
        var agenda = new List<CalendarEvent> { new("Apel inventat", At(9), At(9, 30), CalendarKind.Online) };
        var text = BriefingComposer.Compose(Data(active: 0, agenda: agenda));

        Assert.Contains("N-am înregistrat nimic", text);
        Assert.Contains("Azi în calendar", text);
    }
}
