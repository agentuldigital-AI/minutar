using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Diagnostics;

/// <summary>Ce fel de încetineală e — singura întrebare care contează când daemonul întârzie.</summary>
public enum LatencyVerdict
{
    Ok,

    /// <summary>Cererea a stat la coadă: firele erau ocupate, nu munca ei era grea.</summary>
    Queued,

    /// <summary>Cererea chiar a durat: firul era liber, handlerul e lent.</summary>
    Slow,
}

/// <summary>
/// Măsoară cât întârzie firele daemonului, ca data viitoare să nu mai ghicim.
///
/// Motivul: indicatorul din bară cere starea la două secunde și renunță după una și jumătate.
/// Când daemonul depășește pragul, sare pe „offline", apoi înapoi — și pare că moare, deși
/// procesul e viu. O zi întreagă de căutat cauza a produs două explicații plauzibile și
/// amândouă false, pentru că nu aveam decât timpul TOTAL al unei cereri. Ala nu spune nimic:
/// o secundă poate însemna un handler greu sau o cerere care a așteptat un fir liber, iar
/// cele două se repară complet diferit.
///
/// Cum se separă: la fiecare câteva secunde punem o sarcină banală în coada firelor și
/// măsurăm cât trece până PORNEȘTE. Timpul ăla nu depinde de ce face sarcina — e curat
/// întârzierea de planificare. Pus lângă durata unei cereri lente, răspunde direct:
///
///   cerere lentă + planificare rapidă → handlerul e greu
///   cerere lentă + planificare lentă  → firele sunt înfometate, handlerul e nevinovat
///
/// Costul: o sarcină goală la trei secunde. Poate rămâne pornit mereu, și trebuie, fiindcă
/// pana e intermitentă și trece în câteva minute — exact tipul de defect pe care nu-l prinzi
/// în flagrant dacă instrumentul nu era deja acolo.
/// </summary>
public sealed class LatencyProbe : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(3);

    /// <summary>Peste atât, planificarea e vizibil întârziată — sub, e zgomot de măsurare.</summary>
    public const int QueuedMs = 150;

    /// <summary>O cerere sub atât nu merită o linie de log, oricât de încărcat ar fi sistemul.</summary>
    public const int SlowRequestMs = 400;

    /// <summary>Rezumatul periodic: apare și când nu s-a atins niciun prag, ca să existe o linie de bază.</summary>
    private static readonly TimeSpan SummaryEvery = TimeSpan.FromMinutes(10);

    /// <summary>Ultima întârziere de planificare, citită de middleware-ul de cereri.</summary>
    public static volatile int LastDelayMs;

    /// <summary>
    /// Verdictul, ca funcție pură: e singura regulă din tot fișierul, deci trebuie testabilă
    /// fără fire, fără ceas și fără daemon.
    /// </summary>
    public static LatencyVerdict Verdict(int requestMs, int scheduleDelayMs)
    {
        if (requestMs < SlowRequestMs) return LatencyVerdict.Ok;
        return scheduleDelayMs >= QueuedMs ? LatencyVerdict.Queued : LatencyVerdict.Slow;
    }

    public static string Describe(LatencyVerdict v) => v switch
    {
        LatencyVerdict.Queued => "a asteptat un fir liber (fire infometate)",
        LatencyVerdict.Slow => "handlerul chiar a durat (firele erau libere)",
        _ => "ok",
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var maxDelay = 0;
        var sum = 0L;
        var n = 0;
        var lastSummary = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Every, ct);

                var ms = await ScheduleDelayAsync();
                LastDelayMs = ms;
                maxDelay = Math.Max(maxDelay, ms);
                sum += ms;
                n++;

                if (ms >= QueuedMs)
                {
                    Log.Warn($"[lat] planificare intarziata {ms}ms " +
                             $"(fire={ThreadPool.ThreadCount}, in coada={ThreadPool.PendingWorkItemCount})");
                }

                var now = DateTimeOffset.UtcNow;
                if (now - lastSummary >= SummaryEvery && n > 0)
                {
                    Log.Info($"[lat] ultimele {SummaryEvery.TotalMinutes:0} min: " +
                             $"planificare medie {sum / n}ms, maxim {maxDelay}ms, " +
                             $"fire={ThreadPool.ThreadCount}");
                    lastSummary = now;
                    maxDelay = 0; sum = 0; n = 0;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("[lat] sonda a esuat: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Cât trece până când o sarcină goală PORNEȘTE pe un fir din pool. Sarcina nu face nimic
    /// dinadins: orice muncă în ea ar amesteca durata proprie peste ce vrem să măsurăm.
    /// </summary>
    private static async Task<int> ScheduleDelayAsync()
    {
        var sw = Stopwatch.StartNew();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.UnsafeQueueUserWorkItem(_ => started.TrySetResult(), null);
        await started.Task;
        return (int)sw.ElapsedMilliseconds;
    }
}
