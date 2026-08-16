using Microsoft.Extensions.Hosting;
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

    public BriefingService(ConfigProvider config, ReportService report, DayStateStore days,
                           BriefingStateStore state, TelegramClient telegram)
    {
        _config = config;
        _report = report;
        _days = days;
        _state = state;
        _telegram = telegram;
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

        return BriefingComposer.FromReport(
            BriefingPeriod.Day,
            DateOnly.FromDateTime(today.AddDays(-1)), DateOnly.FromDateTime(today),
            day, week, WeekDays);
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

        return BriefingComposer.FromReport(
            kind, DateOnly.FromDateTime(from), DateOnly.FromDateTime(to), cur, prev);
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
