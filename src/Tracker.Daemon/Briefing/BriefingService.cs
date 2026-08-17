using Microsoft.Extensions.Hosting;
using Tracker.Daemon.Calendar;
using Tracker.Daemon.Coach;
using Tracker.Daemon.Report;
using Tracker.Shared.Config;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Briefing;

/// <summary>
/// Briefingurile pe Telegram, trimise la pornirea daemonului — nu la ore fixe.
///
/// De ce la pornire: laptopul nu stă aprins non-stop, deci o oră fixă ar rata zilele în care
/// nu era pornit. La pornire ajunge exact când te așezi la birou, adică în momentul în care
/// poți chiar face ceva cu mesajul.
///
/// Fiecare briefing e ținut de o cheie a perioadei ȚINTĂ („week:2026-W33"), nu de o zi a
/// săptămânii. Nu verificăm „e luni?": calculăm mereu săptămâna trecută, iar cheia se schimbă
/// singură când începe una nouă. Așa, dacă lunea ai fost plecat, primești marți — nu pierzi
/// săptămâna.
///
/// Marcajul se pune DOAR după livrare reușită, ca o pornire fără rețea să nu piardă briefingul.
///
/// Telefonul e cazul special: datele vin dintr-un import manual, deci nu pot fi așteptate la o
/// oră. Briefingul de săptămână/lună spune că lipsesc, iar sumarul propriu-zis pleacă în
/// momentul importului — vezi <see cref="OnPhoneImportedAsync"/>. Nu e nevoie de nicio
/// programare: ca să imporți, aplicația trebuie deja să meargă.
/// </summary>
public sealed class BriefingService : BackgroundService
{
    /// <summary>Reîncercări: rețeaua de după login e adesea gata cu întârziere.</summary>
    private static readonly TimeSpan[] Retries =
    [
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(4),
    ];

    private const int WeekDays = 7;

    private readonly ConfigProvider _config;
    private readonly ReportService _report;
    private readonly DayStateStore _days;
    private readonly BriefingStateStore _state;
    private readonly TelegramClient _telegram;
    private readonly GoogleCalendarClient _calendar;
    private readonly RescheduleStore _reschedule = new();

    public BriefingService(ConfigProvider config, ReportService report, DayStateStore days,
                           BriefingStateStore state, TelegramClient telegram,
                           GoogleCalendarClient calendar)
    {
        _config = config;
        _report = report;
        _days = days;
        _state = state;
        _telegram = telegram;
        _calendar = calendar;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cfg = _config.Current.Telegram;
        if (!cfg.Enabled) return;
        if (!Configured(cfg))
        {
            Log.Info("Briefing: telegram.enabled = true dar lipsește bot_token sau chat_id — sar peste.");
            return;
        }

        try
        {
            // răgaz după login: bucket-urile se inițializează în fundal, iar rețeaua vine mai târziu
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, cfg.BriefingDelaySeconds)), ct);

            // ...iar dacă pornirea a fost în toiul nopții, mai așteptăm până la o oră la care
            // mesajul chiar ajunge la cineva treaz. Vezi MorningWait.
            var asteapta = MorningWait(DateTimeOffset.Now, cfg.EarliestHour);
            if (asteapta > TimeSpan.Zero)
            {
                Log.Info($"Briefing: pornire la o oră prea devreme, aștept {asteapta.TotalMinutes:F0} min.");
                await Task.Delay(asteapta, ct);
            }

            if (cfg.DailyBriefing) await DailyAsync(ct);
            if (cfg.WeeklyBriefing) await PeriodAsync(BriefingPeriod.Week, ct);
            if (cfg.MonthlyBriefing) await PeriodAsync(BriefingPeriod.Month, ct);
        }
        catch (OperationCanceledException)
        {
            // oprire normală
        }
        catch (Exception ex)
        {
            Log.Error("Briefing failed: " + ex);
        }
    }

    // ---------------------------------------------------------------- zilnic

    private async Task DailyAsync(CancellationToken ct)
    {
        // ziua e singurul briefing cu magazie proprie: DayState e deja „o intrare per zi"
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        if (_days.Load(today).BriefingSent) return;

        var text = BriefingComposer.Compose(await DailyDataAsync(ct));
        if (await SendWithRetryAsync(text, ct))
        {
            // marcăm ziua capturată, nu Today() — o trimitere care trece de miezul nopții
            // nu are voie să consume briefingul zilei următoare
            _days.Mutate(today, s => s.BriefingSent = true);
            Log.Info("Briefing zilnic trimis");
        }
    }

    /// <summary>Textul zilnic. Public: îl folosește și endpointul de test.</summary>
    public async Task<string> ComposeAsync(CancellationToken ct) =>
        BriefingComposer.Compose(await DailyDataAsync(ct));

    private async Task<BriefingData> DailyDataAsync(CancellationToken ct)
    {
        var today = DateTimeOffset.Now.Date;
        var day = await _report.BuildAsync(
            ReportService.LocalMidnight(today.AddDays(-1)), ReportService.LocalMidnight(today), ct);

        // media pe 7 zile dă sens cifrei de ieri; dacă pică, mesajul merge fără ea
        object? week = null;
        try
        {
            week = await _report.BuildAsync(
                ReportService.LocalMidnight(today.AddDays(-WeekDays)), ReportService.LocalMidnight(today), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Briefing: media pe 7 zile a eșuat, trimit fără ea: " + ex.Message);
        }

        var data = BriefingComposer.FromReport(
            BriefingPeriod.Day,
            DateOnly.FromDateTime(today.AddDays(-1)), DateOnly.FromDateTime(today),
            day, week, WeekDays);

        // Două ferestre diferite, dinadins: planul se compară cu IERI (ziua măsurată), iar
        // agenda arată AZI — singurul lucru din mesaj pe care mai poți face ceva.
        return await WithCalendarAsync(data, today.AddDays(-1), today, today, ct);
    }

    // ------------------------------------------------------ saptamanal / lunar

    /// <summary>Textul unei perioade, fără să trimită și fără să marcheze. Pentru endpointul de test.</summary>
    public async Task<string> ComposePeriodAsync(BriefingPeriod kind, CancellationToken ct) =>
        BriefingComposer.Compose(await PeriodDataAsync(kind, ct));

    private async Task PeriodAsync(BriefingPeriod kind, CancellationToken ct)
    {
        var (from, _, _) = Bounds(kind, DateTimeOffset.Now.Date);
        var key = Key(kind, from);
        if (_state.WasSent(key)) return;

        if (await SendWithRetryAsync(BriefingComposer.Compose(await PeriodDataAsync(kind, ct)), ct))
        {
            _state.MarkSent(key);
            Log.Info($"Briefing {kind.ToString().ToLowerInvariant()} trimis ({key})");
        }
    }

    private async Task<BriefingData> PeriodDataAsync(BriefingPeriod kind, CancellationToken ct)
    {
        var (from, to, prevFrom) = Bounds(kind, DateTimeOffset.Now.Date);

        var cur = await _report.BuildAsync(ReportService.LocalMidnight(from), ReportService.LocalMidnight(to), ct);

        object? prev = null;
        try
        {
            prev = await _report.BuildAsync(ReportService.LocalMidnight(prevFrom), ReportService.LocalMidnight(from), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Briefing {kind}: perioada de comparație a eșuat, trimit fără ea: {ex.Message}");
        }

        var data = BriefingComposer.FromReport(
            kind, DateOnly.FromDateTime(from), DateOnly.FromDateTime(to), cur, prev);

        // Fără agendă pe perioadă: o listă cu o lună de evenimente n-ar fi o informație.
        return await WithCalendarAsync(data, from, to, null, ct);
    }

    // --------------------------------------------------------------- calendar

    /// <summary>
    /// Pune peste cifrele măsurate ce spunea calendarul. Nu aruncă și nu blochează: dacă Google
    /// tace, mesajul pleacă exact ca înainte. Un calendar picat n-are voie să oprească
    /// briefingul — cifrele tale sunt deja în raport, calendarul e doar context.
    /// </summary>
    private async Task<BriefingData> WithCalendarAsync(
        BriefingData data, DateTime from, DateTime to, DateTime? agendaFor, CancellationToken ct)
    {
        if (!_calendar.Configured()) return data;

        var cfg = _config.Current.Calendar;
        var planned = Planned(await ReadAsync(from, to, ct));

        IReadOnlyList<CalendarEvent>? agenda = null;
        IReadOnlyList<RescheduleAlert>? postponed = null;

        if (agendaFor is { } zi)
        {
            // O SINGURĂ citire pentru amândouă: agenda e ziua de azi decupată din fereastra pe
            // care oricum o parcurgem ca să vedem ce s-a mutat. Două cereri ar fi fost două
            // ocazii de a pica, pentru aceleași date.
            var zile = cfg.RescheduleAlerts ? Math.Max(1, cfg.RescheduleWindowDays) : 1;
            var window = await ReadAsync(zi, zi.AddDays(zile), ct);

            if (window is not null)
            {
                // suprapunere, nu egalitate de dată: un bloc început ieri și întins peste azi
                // face parte din ziua ta, chiar dacă nu începe azi
                var azi = window.Where(e => e.Start.LocalDateTime < zi.Date.AddDays(1)
                                            && e.End.LocalDateTime > zi.Date)
                                .OrderBy(e => e.Start).ToList();
                if (azi.Count > 0)
                {
                    if (azi.Count > cfg.MaxAgendaItems)
                        Log.Info($"Calendar: agenda de azi are {azi.Count} intrări, intră primele {cfg.MaxAgendaItems}.");
                    agenda = azi.Take(cfg.MaxAgendaItems).ToList();
                }

                if (cfg.RescheduleAlerts)
                {
                    var (stare, alerte) = RescheduleTracker.Observe(
                        _reschedule.Load(), window, DateOnly.FromDateTime(zi), cfg.RescheduleMoves);
                    _reschedule.Save(stare);
                    if (alerte.Count > 0)
                    {
                        Log.Info($"Calendar: {alerte.Count} evenimente mutate de cel puțin {cfg.RescheduleMoves} ori.");
                        postponed = alerte;
                    }
                }
            }
        }

        return data with
        {
            Agenda = agenda,
            AgendaTitles = cfg.AgendaInBriefing,
            PlannedMeetingSeconds = planned.Seconds,
            PlannedMeetingCount = planned.Count,
            PlannedInPersonCount = planned.InPerson,
            Postponed = postponed,
        };
    }

    private Task<List<CalendarEvent>?> ReadAsync(DateTime from, DateTime to, CancellationToken ct) =>
        _calendar.ListAsync(ReportService.LocalMidnight(from), ReportService.LocalMidnight(to), ct);

    /// <summary>
    /// Numai apelurile video se compară cu ce a măsurat tracker-ul. Reuniune, nu sumă: două
    /// apeluri suprapuse înseamnă tot o oră planificată. Programările la o adresă se numără
    /// separat — sunt timp blocat, dar nu în fața calculatorului, deci absența lor din măsurători
    /// nu e o ședință ratată.
    /// </summary>
    private static (double Seconds, int Count, int InPerson) Planned(IReadOnlyList<CalendarEvent>? evs)
    {
        if (evs is null) return (0, 0, 0);
        var online = evs.Where(e => e.Kind == CalendarKind.Online && !e.AllDay).ToList();
        return (CalendarClassifier.UnionSeconds(online), online.Count,
                evs.Count(e => e.Kind == CalendarKind.InPerson && !e.AllDay));
    }

    /// <summary>
    /// Cât mai are de așteptat un briefing pornit prea devreme. Zero = poate pleca acum.
    ///
    /// Motivul e o scăpare descoperită pe viu: o instalare rulată seara târziu repornește
    /// daemonul, briefingul pleacă la 00:04 — ora la care dormi — ȘI marchează ziua ca trimisă,
    /// deci dimineața nu mai vine nimic. Pierdeai briefingul exact în ziua în care actualizai
    /// aplicația.
    ///
    /// E o PODEA, nu o programare la oră fixă: dacă pornești calculatorul la 9, îl primești la
    /// 9, nu se așteaptă nimic. Se așteaptă doar când ai pornit înaintea ei.
    ///
    /// Pură și publică pentru că e singura parte cu reguli — restul e Task.Delay.
    /// </summary>
    public static TimeSpan MorningWait(DateTimeOffset now, int earliestHour)
    {
        if (earliestHour <= 0 || now.Hour >= earliestHour) return TimeSpan.Zero;
        var target = now.Date.AddHours(earliestHour);
        var wait = target - now.DateTime;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <summary>Perioada ÎNCHEIATĂ dinaintea zilei curente, plus cea de dinaintea ei, pentru comparație.</summary>
    public static (DateTime From, DateTime To, DateTime PrevFrom) Bounds(BriefingPeriod kind, DateTime today)
    {
        if (kind == BriefingPeriod.Week)
        {
            var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // luni = prima zi
            return (thisMonday.AddDays(-7), thisMonday, thisMonday.AddDays(-14));
        }
        var firstOfMonth = new DateTime(today.Year, today.Month, 1);
        return (firstOfMonth.AddMonths(-1), firstOfMonth, firstOfMonth.AddMonths(-2));
    }

    private static string Key(BriefingPeriod kind, DateTime from) => kind == BriefingPeriod.Week
        ? $"week:{System.Globalization.ISOWeek.GetYear(from):0000}-W{System.Globalization.ISOWeek.GetWeekOfYear(from):00}"
        : $"month:{from:yyyy-MM}";

    // ----------------------------------------------------------------- telefon

    /// <summary>
    /// Apelat din endpointul de import, după ce scrierea a reușit. Trimite sumarul perioadei
    /// tocmai importate: telefon, PC și totalul lor.
    ///
    /// Nu are nevoie de programare — dacă ai putut importa, aplicația merge. Nu aruncă
    /// niciodată: un Telegram picat n-are voie să strice un import care s-a salvat deja.
    /// </summary>
    public async Task OnPhoneImportedAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        try
        {
            var cfg = _config.Current.Telegram;
            if (!cfg.Enabled || !cfg.PhoneImportBriefing || !Configured(cfg)) return;

            var key = $"phone:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}";
            if (_state.WasSent(key)) return;

            var rep = await _report.BuildAsync(
                ReportService.LocalMidnight(from.ToDateTime(TimeOnly.MinValue)),
                ReportService.LocalMidnight(to.ToDateTime(TimeOnly.MinValue)), ct);

            var data = BriefingComposer.FromReport(BriefingPeriod.PhoneImport, from, to, rep);
            if (await SendWithRetryAsync(BriefingComposer.Compose(data), ct))
            {
                _state.MarkSent(key);
                Log.Info($"Briefing telefon trimis ({key})");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Briefing telefon a eșuat: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ comun

    private async Task<bool> SendWithRetryAsync(string text, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var cfg = _config.Current.Telegram; // reluat: token-ul poate fi completat între încercări
            if (Configured(cfg) && await _telegram.SendAsync(cfg.BotToken.Trim(), cfg.ChatId.Trim(), text, ct))
                return true;

            if (attempt >= Retries.Length)
            {
                Log.Warn("Briefing: nelivrat după toate reîncercările — se reia la următoarea pornire.");
                return false;
            }
            await Task.Delay(Retries[attempt], ct);
        }
    }

    private static bool Configured(TelegramConfig c) =>
        c.BotToken.Trim().Length > 0 && c.ChatId.Trim().Length > 0;
}
