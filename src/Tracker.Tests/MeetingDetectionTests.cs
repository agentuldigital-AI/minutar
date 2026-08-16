using System.Text.Json;
using Tracker.Daemon.Report;
using Tracker.Shared.Aw;
using Tracker.Shared.Config;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// Detectarea ședințelor video.
///
/// Problema: o ședință de o oră putea să apară ca zece minute, pentru că restul timpului
/// fereastra din față era browserul cu documentul partajat. „Timp în ședință" și „timp
/// cu aplicația de apel în față" sunt întrebări diferite, iar trackerul o măsura doar pe a doua.
///
/// Regula pe care o apără testele: semnalul e TITLUL ferestrei, nu simpla prezență a procesului.
/// Zoom scrie „Zoom Meeting" în apel și „Zoom Workplace" când e doar deschis — fără distincția
/// asta, o aplicație lăsată pornită toată ziua ar produce o ședință de opt ore.
/// </summary>
public class MeetingDetectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 16, 9, 0, 0, TimeSpan.FromHours(3));

    private static AwEvent Win(double atMinutes, double minutes, string app, string title) =>
        new(T0.AddMinutes(atMinutes), minutes * 60,
            JsonDocument.Parse($$"""{"app":"{{app}}","title":"{{title}}"}""").RootElement.Clone());

    private static AwEvent Web(double atMinutes, double minutes, string url) =>
        new(T0.AddMinutes(atMinutes), minutes * 60,
            JsonDocument.Parse($$"""{"url":"{{url}}"}""").RootElement.Clone());

    /// <summary>Activ toată ziua, ca testele să izoleze detecția, nu intersecția cu AFK.</summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> AllActive() =>
        new() { (T0.AddMinutes(-60), T0.AddMinutes(600)) };

    private static MeetingsConfig Cfg(int bridge = 25, int min = 3) =>
        new() { BridgeMinutes = bridge, MinMinutes = min };

    [Fact]
    public void ApelulAcoperaSiFerestreleDintreEl()
    {
        // tiparul tipic: apel, apoi document partajat, apoi iar apel = o singura sedinta
        var w = new List<AwEvent>
        {
            Win(0, 12, "Zoom.exe", "Zoom Meeting"),
            Win(12, 20, "chrome.exe", "Buget - Google Sheets"),
            Win(32, 8, "Zoom.exe", "Zoom Meeting"),
        };

        var m = ReportService.DetectMeetings(w, new List<AwEvent>(), AllActive(), Cfg());

        Assert.Single(m);
        Assert.Equal(40, (m[0].End - m[0].Start).TotalMinutes);
    }

    [Fact]
    public void AplicatiaDoarDeschisa_NuEsteSedinta()
    {
        // fara asta, un Zoom lasat pornit toata ziua ar produce o sedinta de opt ore
        var w = new List<AwEvent> { Win(0, 300, "Zoom.exe", "Zoom Workplace") };

        Assert.Empty(ReportService.DetectMeetings(w, new List<AwEvent>(), AllActive(), Cfg()));
    }

    [Fact]
    public void ApelDeAltaAplicatie_NuEsteLuatInSeama()
    {
        var w = new List<AwEvent> { Win(0, 30, "Notepad.exe", "Meeting notes") };

        Assert.Empty(ReportService.DetectMeetings(w, new List<AwEvent>(), AllActive(), Cfg()));
    }

    [Fact]
    public void DouaApeluriDepartate_RaminDouaSedinte()
    {
        var w = new List<AwEvent>
        {
            Win(0, 30, "Zoom.exe", "Zoom Meeting"),
            Win(120, 30, "Zoom.exe", "Zoom Meeting"),   // dupa 90 min de pauza
        };

        Assert.Equal(2, ReportService.DetectMeetings(w, new List<AwEvent>(), AllActive(), Cfg()).Count);
    }

    [Fact]
    public void PauzaSubPunte_NuRupeSedinta()
    {
        var w = new List<AwEvent>
        {
            Win(0, 10, "Zoom.exe", "Zoom Meeting"),
            Win(30, 10, "Zoom.exe", "Zoom Meeting"),   // gaura de 20 min < puntea de 25
        };

        Assert.Single(ReportService.DetectMeetings(w, new List<AwEvent>(), AllActive(), Cfg(bridge: 25)));
    }

    [Fact]
    public void ClickRatacit_NuDevineSedinta()
    {
        var w = new List<AwEvent> { Win(0, 1, "Zoom.exe", "Zoom Meeting") };

        Assert.Empty(ReportService.DetectMeetings(w, new List<AwEvent>(), AllActive(), Cfg(min: 3)));
    }

    [Fact]
    public void ApelDinBrowser_EsteRecunoscutDupaDomeniu()
    {
        // Meet nu are titlu de proces distinct — se vede doar in bucketul web
        var web = new List<AwEvent> { Web(0, 45, "https://meet.google.com/abc-defg-hij") };

        var m = ReportService.DetectMeetings(new List<AwEvent>(), web, AllActive(), Cfg());

        Assert.Single(m);
        Assert.Equal(45, (m[0].End - m[0].Start).TotalMinutes);
    }

    [Fact]
    public void TimpulAfkNuIntraInSedinta()
    {
        // apelul merge, dar tu ai plecat de la birou: „activ" se opreste, deci si sedinta
        var w = new List<AwEvent> { Win(0, 60, "Zoom.exe", "Zoom Meeting") };
        var active = new List<(DateTimeOffset Start, DateTimeOffset End)> { (T0, T0.AddMinutes(20)) };

        var m = ReportService.DetectMeetings(w, new List<AwEvent>(), active, Cfg());

        Assert.Single(m);
        Assert.Equal(20, (m[0].End - m[0].Start).TotalMinutes);
    }

    [Fact]
    public void FaraNiciUnSemnal_NuInventeazaSedinte()
    {
        var w = new List<AwEvent> { Win(0, 120, "chrome.exe", "Ceva") };

        Assert.Empty(ReportService.DetectMeetings(w, new List<AwEvent>(), AllActive(), Cfg()));
    }

    [Fact]
    public void ControalelDePartajare_ContinuaSedinta()
    {
        // titlurile auxiliare din timpul partajarii sunt tot semnal de apel in curs
        var w = new List<AwEvent>
        {
            Win(0, 5, "Zoom.exe", "Zoom Meeting"),
            Win(5, 25, "Zoom.exe", "Screen sharing meeting controls"),
        };

        var m = ReportService.DetectMeetings(w, new List<AwEvent>(), AllActive(), Cfg());

        Assert.Single(m);
        Assert.Equal(30, (m[0].End - m[0].Start).TotalMinutes);
    }
}
