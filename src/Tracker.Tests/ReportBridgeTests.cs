using System.Text.Json;
using Tracker.Daemon.Report;
using Tracker.Shared.Aw;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// Fix 2026-07-11: sub-3s gaps between consecutive window events (the per-switch crumbs,
/// ~48 min/day measured) get absorbed into the PRECEDING event; bigger gaps stay real.
/// </summary>
public sealed class ReportBridgeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    private static AwEvent Ev(double atSeconds, double duration)
    {
        using var doc = JsonDocument.Parse("""{"app":"x.exe","title":"t"}""");
        return new AwEvent(T0.AddSeconds(atSeconds), duration, doc.RootElement.Clone());
    }

    [Fact]
    public void SmallGap_IsAbsorbedIntoPrecedingEvent()
    {
        var outp = ReportService.BridgeWindowGaps(new List<AwEvent> { Ev(0, 10), Ev(11.5, 5) });
        Assert.Equal(11.5, outp[0].Duration, 3); // 10s + gap de 1.5s
        Assert.Equal(5, outp[1].Duration, 3);
    }

    [Fact]
    public void LargeGap_StaysUntouched()
    {
        var outp = ReportService.BridgeWindowGaps(new List<AwEvent> { Ev(0, 10), Ev(20, 5) });
        Assert.Equal(10, outp[0].Duration, 3); // gap de 10s > prag — gol real, rămâne
    }

    [Fact]
    public void OverlappingOrAdjacent_NotExtended()
    {
        var outp = ReportService.BridgeWindowGaps(new List<AwEvent> { Ev(0, 10), Ev(10, 5), Ev(14, 5) });
        Assert.Equal(10, outp[0].Duration, 3);  // adiacent perfect: gap 0 → neatins
        Assert.Equal(5, outp[1].Duration, 3);   // suprapus (gap negativ) → neatins
    }

    [Fact]
    public void UnsortedInput_IsSortedThenBridged()
    {
        var outp = ReportService.BridgeWindowGaps(new List<AwEvent> { Ev(11.5, 5), Ev(0, 10) });
        Assert.Equal(T0, outp[0].Timestamp);
        Assert.Equal(11.5, outp[0].Duration, 3);
    }

    [Fact]
    public void ZeroDurationFlash_GetsItsRealSecond()
    {
        // fereastră văzută o singură dată (durata 0), urmată la 1s de următoarea:
        // fărâma îi aparține — altfel dispărea complet din felii
        var outp = ReportService.BridgeWindowGaps(new List<AwEvent> { Ev(0, 0), Ev(1, 5) });
        Assert.Equal(1, outp[0].Duration, 3);
    }
}

/// <summary>
/// Fix 2026-07-11 (al doilea): cusăturile de ~5s dintre intervalele active (schimbări de
/// proiect/clasă la cadență de heartbeat 5s) se îmbină cu o punte de 15s; pauzele reale
/// (≥3 min până la AFK) rămân separate.
/// </summary>
public sealed class MergeIntervalsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    private static AwEvent Ev(double atSeconds, double duration)
    {
        using var doc = JsonDocument.Parse("""{"project":"x","class":"productive"}""");
        return new AwEvent(T0.AddSeconds(atSeconds), duration, doc.RootElement.Clone());
    }

    [Fact]
    public void FiveSecondSeam_IsBridged()
    {
        var merged = ReportService.MergeIntervals(new List<AwEvent> { Ev(0, 60), Ev(65, 60) });
        var iv = Assert.Single(merged);
        Assert.Equal(T0, iv.Start);
        Assert.Equal(T0.AddSeconds(125), iv.End);
    }

    [Fact]
    public void GapBeyondBridge_StaysSeparate()
    {
        var merged = ReportService.MergeIntervals(new List<AwEvent> { Ev(0, 60), Ev(80, 60) });
        Assert.Equal(2, merged.Count); // 20s > punte 15s — gol real, rămâne
    }

    [Fact]
    public void RealPause_NeverBridged()
    {
        var merged = ReportService.MergeIntervals(new List<AwEvent> { Ev(0, 60), Ev(300, 60) });
        Assert.Equal(2, merged.Count);
    }
}
