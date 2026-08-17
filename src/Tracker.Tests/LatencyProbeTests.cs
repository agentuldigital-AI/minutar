using Tracker.Daemon.Diagnostics;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// Verdictul sondei de latență.
///
/// Regula pe care o apără: timpul total al unei cereri nu spune de unul singur nimic. O secundă
/// poate însemna un handler greu SAU o cerere care a așteptat un fir liber, iar cele două se
/// repară complet diferit. O zi de căutat cauza a produs două explicații plauzibile și amândouă
/// false, fiindcă lipsea exact distincția asta.
/// </summary>
public class LatencyProbeTests
{
    [Fact]
    public void CerereRapida_NuEsteNiciodataProblema()
    {
        // chiar și cu firele înfometate, o cerere care s-a terminat repede nu merită o linie
        Assert.Equal(LatencyVerdict.Ok, LatencyProbe.Verdict(10, 0));
        Assert.Equal(LatencyVerdict.Ok, LatencyProbe.Verdict(10, 5_000));
    }

    [Fact]
    public void CerereLentaCuPlanificareRapida_EsteHandlerulGreu()
    {
        // firul era liber, deci munca din handler chiar a durat
        Assert.Equal(LatencyVerdict.Slow, LatencyProbe.Verdict(2_000, 3));
    }

    [Fact]
    public void CerereLentaCuPlanificareLenta_EsteCoada()
    {
        // handlerul e nevinovat: n-a avut pe ce fir să pornească
        Assert.Equal(LatencyVerdict.Queued, LatencyProbe.Verdict(2_000, 900));
    }

    [Theory]
    [InlineData(LatencyProbe.SlowRequestMs - 1, 10_000, LatencyVerdict.Ok)]
    [InlineData(LatencyProbe.SlowRequestMs, LatencyProbe.QueuedMs, LatencyVerdict.Queued)]
    [InlineData(LatencyProbe.SlowRequestMs, LatencyProbe.QueuedMs - 1, LatencyVerdict.Slow)]
    public void PragurileSuntInclusive(int requestMs, int delayMs, LatencyVerdict asteptat)
    {
        Assert.Equal(asteptat, LatencyProbe.Verdict(requestMs, delayMs));
    }

    [Fact]
    public void FiecareVerdictAreOExplicatieInRomana()
    {
        // logul e citit la câteva săptămâni după ce s-a scris, de cineva care a uitat contextul
        Assert.Contains("fir", LatencyProbe.Describe(LatencyVerdict.Queued));
        Assert.Contains("durat", LatencyProbe.Describe(LatencyVerdict.Slow));
    }
}
