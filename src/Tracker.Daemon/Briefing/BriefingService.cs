using Microsoft.Extensions.Hosting;
using Tracker.Daemon.Coach;
using Tracker.Daemon.Report;
using Tracker.Shared.Config;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Briefing;

/// <summary>
/// Briefingul zilnic pe Telegram, trimis la pornirea daemonului — nu la o oră fixă.
///
/// De ce la pornire: laptopul nu stă aprins non-stop, deci o oră fixă ar rata zilele în care nu
/// era pornit. La pornire ajunge exact când te așezi la birou, adică în momentul în care poți
/// chiar face ceva cu el (aceeași logică de „livrează la breakpoint" ca la nudge-uri).
///
/// O singură dată pe zi calendaristică, marcat în DayState — altfel trei reporniri însemnau trei
/// mesaje și, din prima săptămână, zgomot. Marcajul se pune DOAR după livrare reușită, ca o
/// pornire fără rețea să nu piardă briefingul zilei.
/// </summary>
public sealed class BriefingService : BackgroundService
{
    /// <summary>Reîncercări la pornire: rețeaua de după login e adesea gata cu întârziere.</summary>
    private static readonly TimeSpan[] Retries =
    [
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(4),
    ];

    private const int WeekDays = 7;

    private readonly ConfigProvider _config;
    private readonly ReportService _report;
    private readonly DayStateStore _days;
    private readonly TelegramClient _telegram;

    public BriefingService(ConfigProvider config, ReportService report, DayStateStore days, TelegramClient telegram)
    {
        _config = config;
        _report = report;
        _days = days;
        _telegram = telegram;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cfg = _config.Current.Telegram;
        if (!cfg.Enabled || !cfg.DailyBriefing) return;
        if (!Configured(cfg))
        {
            Log.Info("Briefing: telegram.enabled = true dar lipsește bot_token sau chat_id — sar peste.");
            return;
        }

        try
        {
            // răgaz după login: bucket-urile se inițializează în fundal, iar rețeaua vine mai târziu
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, cfg.BriefingDelaySeconds)), ct);
            await SendIfDueAsync(ct);
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

    private async Task SendIfDueAsync(CancellationToken ct)
    {
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        if (_days.Load(today).BriefingSent) return;

        var text = await ComposeAsync(ct);

        for (var attempt = 0; ; attempt++)
        {
            var cfg = _config.Current.Telegram; // reluat: token-ul poate fi completat între încercări
            if (Configured(cfg) && await _telegram.SendAsync(cfg.BotToken.Trim(), cfg.ChatId.Trim(), text, ct))
            {
                // marcăm ziua capturată, nu Today() — o trimitere care trece de miezul nopții
                // nu are voie să consume briefingul zilei următoare
                _days.Mutate(today, s => s.BriefingSent = true);
                Log.Info("Briefing sent to Telegram");
                return;
            }

            if (attempt >= Retries.Length)
            {
                Log.Warn("Briefing: nelivrat după toate reîncercările — se va relua la următoarea pornire.");
                return;
            }
            await Task.Delay(Retries[attempt], ct);
        }
    }

    /// <summary>Textul pentru ziua de ieri. Public: îl folosește și endpointul de test.</summary>
    public async Task<string> ComposeAsync(CancellationToken ct)
    {
        var todayMidnight = ReportService.LocalMidnight(DateTimeOffset.Now.Date);
        var yesterday = ReportService.LocalMidnight(DateTimeOffset.Now.Date.AddDays(-1));
        var weekStart = ReportService.LocalMidnight(DateTimeOffset.Now.Date.AddDays(-WeekDays));

        var day = await _report.BuildAsync(yesterday, todayMidnight, ct);

        // media pe 7 zile dă sens cifrei de ieri; dacă pică, mesajul merge fără ea
        object? week = null;
        try
        {
            week = await _report.BuildAsync(weekStart, todayMidnight, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Briefing: media pe 7 zile a eșuat, trimit fără ea: " + ex.Message);
        }

        return BriefingComposer.Compose(
            BriefingComposer.FromReport(DateOnly.FromDateTime(yesterday.Date), day, week, WeekDays));
    }

    private static bool Configured(TelegramConfig c) =>
        c.BotToken.Trim().Length > 0 && c.ChatId.Trim().Length > 0;
}
