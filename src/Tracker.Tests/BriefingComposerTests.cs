using System.Globalization;
using System.IO;
using Tracker.Daemon.Briefing;
using Tracker.Shared.Config;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// ConfigWriter.Serialize scrie MANUAL fiecare secțiune, iar șase endpointuri rescriu configul în
/// timpul rulării (salvare setări, clasificare telefon, alocări). O secțiune — sau un câmp — uitat
/// acolo dispare tăcut la prima salvare din dashboard, adică token-ul de Telegram s-ar șterge
/// singur. Testul ăsta e paznicul acelei capcane și trebuie extins la FIECARE câmp nou.
/// </summary>
public class TelegramConfigRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tracker-tests", Guid.NewGuid().ToString("N"));

    public TelegramConfigRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SectiuneaTelegram_SupravietuiesteUneiSalvari()
    {
        var path = Path.Combine(_dir, "tracker.toml");
        var cfg = new TrackerConfig();
        cfg.Telegram.Enabled = true;
        cfg.Telegram.BotToken = "123456:ABC-DEF_ghi";
        cfg.Telegram.ChatId = "987654321";
        cfg.Telegram.DailyBriefing = false;
        cfg.Telegram.WeeklyBriefing = false;
        cfg.Telegram.MonthlyBriefing = false;
        cfg.Telegram.PhoneImportBriefing = false;
        cfg.Telegram.BriefingDelaySeconds = 40;
        cfg.Meetings.Enabled = false;
        cfg.Meetings.BridgeMinutes = 40;
        cfg.Meetings.Apps = new List<string> { "Zoom.exe" };

        ConfigWriter.Write(cfg, path);
        var r = TrackerConfig.Load(path);

        Assert.True(r.Telegram.Enabled);
        Assert.Equal("123456:ABC-DEF_ghi", r.Telegram.BotToken);
        Assert.Equal("987654321", r.Telegram.ChatId);
        Assert.False(r.Telegram.DailyBriefing);
        Assert.False(r.Telegram.WeeklyBriefing);
        Assert.False(r.Telegram.MonthlyBriefing);
        Assert.False(r.Telegram.PhoneImportBriefing);
        Assert.Equal(40, r.Telegram.BriefingDelaySeconds);
        Assert.False(r.Meetings.Enabled);
        Assert.Equal(40, r.Meetings.BridgeMinutes);
        Assert.Equal(new[] { "Zoom.exe" }, r.Meetings.Apps);
    }

    [Fact]
    public void TokenLipsaCuEnabledTrue_NuInvalideazaConfigul()
    {
        // dacă ar arunca, ConfigProvider ar păstra tăcut configul vechi la hot-reload
        // și userul ar crede că a salvat
        var cfg = new TrackerConfig();
        cfg.Telegram.Enabled = true;

        cfg.Validate();
    }
}

/// <summary>
/// Ferestrele briefingurilor periodice. Nu verificăm „e luni?" nicăieri: calculăm mereu perioada
/// ÎNCHEIATĂ, iar cheia ei se schimbă singură când începe una nouă. Consecința pe care o apără
/// testele: dacă lunea ai fost plecat, marți primești tot săptămâna trecută — nu o pierzi.
/// </summary>
public class BriefingBoundsTests
{
    [Theory]
    [InlineData("2026-08-16", "2026-08-03", "2026-08-10")] // duminică → tot săptămâna 3–9
    [InlineData("2026-08-10", "2026-08-03", "2026-08-10")] // luni, prima zi a săptămânii noi
    [InlineData("2026-08-12", "2026-08-03", "2026-08-10")] // miercuri, aceeași săptămână țintă
    [InlineData("2026-08-09", "2026-07-27", "2026-08-03")] // duminica dinainte → săptămâna precedentă
    public void Saptamana_EsteMereuCeaIncheiata(string today, string from, string to)
    {
        var b = BriefingService.Bounds(BriefingPeriod.Week, DateTime.Parse(today, CultureInfo.InvariantCulture));

        Assert.Equal(DateTime.Parse(from, CultureInfo.InvariantCulture), b.From);
        Assert.Equal(DateTime.Parse(to, CultureInfo.InvariantCulture), b.To);
        Assert.Equal(7, (b.To - b.From).TotalDays);
        Assert.Equal(7, (b.From - b.PrevFrom).TotalDays); // comparația e săptămâna dinaintea ei
    }

    [Theory]
    [InlineData("2026-08-16", "2026-07-01", "2026-08-01")]
    [InlineData("2026-08-01", "2026-07-01", "2026-08-01")] // prima zi a lunii noi
    [InlineData("2026-01-15", "2025-12-01", "2026-01-01")] // peste granița de an
    [InlineData("2026-03-10", "2026-02-01", "2026-03-01")] // februarie, lună scurtă
    public void Luna_EsteMereuCeaIncheiata(string today, string from, string to)
    {
        var b = BriefingService.Bounds(BriefingPeriod.Month, DateTime.Parse(today, CultureInfo.InvariantCulture));

        Assert.Equal(DateTime.Parse(from, CultureInfo.InvariantCulture), b.From);
        Assert.Equal(DateTime.Parse(to, CultureInfo.InvariantCulture), b.To);
        Assert.Equal(b.From.AddMonths(-1), b.PrevFrom);
    }
}

/// <summary>
/// Textul briefingului. Compunerea e pură (cifre → text), deci se testează fără rețea și fără
/// daemon — exact motivul pentru care e separată de trimitere.
///
/// Ce apără testele:
///  - o perioadă goală nu produce un mesaj cu „0m" peste tot, ci o propoziție care spune asta;
///  - numele de aplicații ajung în HTML, deci trebuie escapate (un „AT&amp;T" strica mesajul);
///  - lipsa datelor de telefon pe săptămână/lună se SPUNE, altfel ai crede că n-ai stat pe telefon;
///  - raportul vine ca tip anonim serializat, iar câmpurile lipsă nu au voie să arunce.
/// </summary>
public class BriefingComposerTests
{
    private static IReadOnlyList<TopItem> Items(params (string, double)[] xs) =>
        xs.Select(x => new TopItem(x.Item1, x.Item2)).ToList();

    private static BriefingData Data(
        BriefingPeriod kind = BriefingPeriod.Day,
        string from = "2026-08-14", string to = "2026-08-15",
        double active = 6 * 3600 + 12 * 60,
        double prod = 4 * 3600, double neutral = 3600, double unprod = 72 * 60,
        double phoneMin = 0, double compare = 0,
        IReadOnlyList<TopItem>? topProd = null, IReadOnlyList<TopItem>? topUnp = null,
        bool phoneMissing = false,
        double phProd = 0, double phNeu = 0, double phUnp = 0, double phUncl = 0,
        IReadOnlyList<TopItem>? phTopProd = null, IReadOnlyList<TopItem>? phTopUnp = null,
        double meetSec = 0, int meetCount = 0) =>
        new(kind, DateOnly.Parse(from, CultureInfo.InvariantCulture), DateOnly.Parse(to, CultureInfo.InvariantCulture),
            active, prod, neutral, unprod, phoneMin, topProd, topUnp, meetSec, meetCount,
            phProd, phNeu, phUnp, phUncl, phTopProd, phTopUnp, compare, phoneMissing);

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(59, "1m")]
    [InlineData(60, "1m")]
    [InlineData(44 * 60, "44m")]
    [InlineData(3600, "1h 00m")]
    [InlineData(6 * 3600 + 12 * 60, "6h 12m")]
    public void Dur_FormateazaScurt(double seconds, string expected) =>
        Assert.Equal(expected, BriefingComposer.Dur(seconds));

    [Fact]
    public void ZiCuActivitate_ContineTotalulSiClasele()
    {
        var text = BriefingComposer.Compose(Data());

        Assert.Contains("6h 12m", text);
        Assert.Contains("Productiv 4h 00m", text);
        Assert.Contains("Neutru 1h 00m", text);
        Assert.Contains("Neproductiv 1h 12m", text);
    }

    [Fact]
    public void ProcenteleSuntRaportateLaTotalulActiv()
    {
        var text = BriefingComposer.Compose(
            Data(active: 8 * 3600, prod: 4 * 3600, neutral: 2 * 3600, unprod: 2 * 3600));

        Assert.Contains("\nProductiv 4h 00m (50%)", text);
        Assert.Contains("\nNeutru 2h 00m (25%)", text);
    }

    [Fact]
    public void PerioadaGoala_SpuneAsta_NuInsiraZerouri()
    {
        var text = BriefingComposer.Compose(Data(active: 0, prod: 0, neutral: 0, unprod: 0));

        Assert.Contains("N-am înregistrat nimic", text);
        Assert.DoesNotContain("0m", text);
    }

    [Fact]
    public void FaraPc_DarCuTelefon_RaporteazaDoarTelefonul()
    {
        var text = BriefingComposer.Compose(Data(active: 0, prod: 0, neutral: 0, unprod: 0, phoneMin: 204));

        Assert.Contains("Pe calculator, nimic", text);
        Assert.Contains("Telefon 3h 24m", text);
        Assert.DoesNotContain("N-am înregistrat nimic", text);
    }

    [Fact]
    public void CuAmbeleDispozitive_ApareSiTotalulComun()
    {
        // 2h PC + 1h telefon: cifra care conteaza cu adevarat e suma, nu fiecare separat
        var text = BriefingComposer.Compose(Data(active: 2 * 3600, phoneMin: 60));

        Assert.Contains("<b>Telefon 1h 00m</b>", text);
        Assert.Contains("<b>Timp total 3h 00m</b>", text);
        Assert.Contains("calculator + telefon", text);
        // totalul e PRIMUL, nu la coada
        Assert.True(text.IndexOf("Timp total", StringComparison.Ordinal)
                    < text.IndexOf("Calculator", StringComparison.Ordinal));
    }

    [Fact]
    public void FaraDateDeTelefon_NuInventeazaTotalulComun()
    {
        // fara telefon, „calculator + telefon" ar minti despre ce insumeaza
        Assert.DoesNotContain("Timp total", BriefingComposer.Compose(Data(phoneMin: 0)));
        Assert.DoesNotContain("Timp total", BriefingComposer.Compose(
            Data(active: 0, prod: 0, neutral: 0, unprod: 0, phoneMin: 200)));
    }

    [Fact]
    public void TelefonulAreDefalcareaSiTopulLui_NuDoarUnTotal()
    {
        // un telefon poate fi 47% neproductiv in timp ce calculatorul e 74% productiv;
        // o singura medie ar ascunde exact partea care conteaza
        var text = BriefingComposer.Compose(Data(
            active: 2 * 3600, phoneMin: 600,
            phProd: 60, phNeu: 120, phUnp: 300, phUncl: 120,
            phTopUnp: Items(("MemeBox", 300 * 60d))));

        Assert.Contains("<b>Calculator 2h 00m</b>", text);
        Assert.Contains("<b>Telefon 10h 00m</b>", text);
        Assert.Contains("Neproductiv 5h 00m (50%)", text);   // raportat la totalul TELEFONULUI
        Assert.Contains("Nedetaliat 2h 00m (20%)", text);
        Assert.Contains("MemeBox 5h 00m", text);
        Assert.Contains("<b>Timp total 12h 00m</b>", text);
    }

    [Fact]
    public void GolulDeTelefon_SeCalculeazaSiCandClaseleNuAcoperaTotalul()
    {
        // Apple da un total mai mare decat suma aplicatiilor numite; fara linia „Nedetaliat"
        // procentele par sa nu se inchida si ai crede ca lipseste ceva din raport
        var text = BriefingComposer.Compose(Data(
            active: 3600, phoneMin: 100, phProd: 10, phNeu: 10, phUnp: 20, phUncl: 0));

        Assert.Contains("Nedetaliat 1h 00m (60%)", text);
    }

    [Fact]
    public void ComparatiaApareDoarCandExista_SiCuEtichetaPotrivita()
    {
        Assert.DoesNotContain("media", BriefingComposer.Compose(Data(compare: 0)));
        Assert.Contains("media ultimelor 7 zile: 5h 48m",
            BriefingComposer.Compose(Data(compare: 5 * 3600 + 48 * 60)));
        Assert.Contains("săptămâna dinainte: 20h 00m",
            BriefingComposer.Compose(Data(kind: BriefingPeriod.Week, compare: 20 * 3600)));
        Assert.Contains("luna dinainte: 80h 00m",
            BriefingComposer.Compose(Data(kind: BriefingPeriod.Month, compare: 80 * 3600)));
    }

    [Fact]
    public void ClaseleSubUnMinut_NuIntraInMesaj()
    {
        // altfel apărea „Neproductiv 0m (0%)", care e zgomot, nu informație
        var text = BriefingComposer.Compose(Data(active: 3600, prod: 3600, neutral: 0, unprod: 20));

        Assert.Contains("Productiv", text);
        Assert.DoesNotContain("Neutru", text);
        Assert.DoesNotContain("Neproductiv", text);
    }

    [Fact]
    public void NumeleDeAplicatii_SuntEscapateCaHtml()
    {
        var text = BriefingComposer.Compose(Data(topProd: Items(("AT&T <Research>", 3600d))));

        Assert.Contains("AT&amp;T &lt;Research&gt;", text);
        Assert.DoesNotContain("<Research>", text);
    }

    [Fact]
    public void TopurileSuntPeClasa_SiEtichetatePeIntelesulOricui()
    {
        var text = BriefingComposer.Compose(Data(
            topProd: Items(("claude.exe", 7200d)), topUnp: Items(("memebox.example", 3600d))));

        Assert.Contains("Top activități productive", text);
        Assert.Contains("Top activități neproductive", text);
        // productivele apar INAINTEA neproductivelor
        Assert.True(text.IndexOf("productive", StringComparison.Ordinal)
                    < text.IndexOf("neproductive", StringComparison.Ordinal));
    }

    [Fact]
    public void FiecareClasaStaPeRandulEi()
    {
        var text = BriefingComposer.Compose(Data(active: 4 * 3600, prod: 2 * 3600, neutral: 3600, unprod: 3600));

        // pe un ecran de telefon, trei perechi pe acelasi rand se citesc greu
        Assert.DoesNotContain("· Neutru", text);
        Assert.Contains("\nProductiv ", text);
        Assert.Contains("\nNeutru ", text);
        Assert.Contains("\nNeproductiv ", text);
    }

    [Fact]
    public void TopGol_NuLasaOEticheraFaraNimicSubEa()
    {
        var text = BriefingComposer.Compose(Data(topProd: null, topUnp: null));

        Assert.DoesNotContain("Top activități", text);
    }

    [Fact]
    public void SedinteleApar_DoarPeSaptamanaSiLuna()
    {
        // pe zi stii oricum ca ai avut o sedinta; intrebarea utila e una de perioada
        Assert.DoesNotContain("Ședințe", BriefingComposer.Compose(
            Data(active: 4 * 3600, meetSec: 3600, meetCount: 2)));

        var w = BriefingComposer.Compose(
            Data(kind: BriefingPeriod.Week, active: 4 * 3600, meetSec: 3600, meetCount: 2));
        Assert.Contains("Ședințe 1h 00m (2 ședințe) · 25% din timpul pe calculator", w);
    }

    [Fact]
    public void OSinguraSedinta_ScrieLaSingular()
    {
        var text = BriefingComposer.Compose(
            Data(kind: BriefingPeriod.Month, active: 10 * 3600, meetSec: 3600, meetCount: 1));

        Assert.Contains("(1 ședință)", text);
    }

    [Fact]
    public void FaraSedinte_NuApareRandul()
    {
        Assert.DoesNotContain("Ședințe", BriefingComposer.Compose(
            Data(kind: BriefingPeriod.Week, meetSec: 0, meetCount: 0)));
    }

    [Fact]
    public void AntetulSpuneCePerioadaE()
    {
        Assert.StartsWith("<b>Ieri, ", BriefingComposer.Compose(Data()));
        Assert.Contains("Săptămâna trecută, 10–16 august",
            BriefingComposer.Compose(Data(kind: BriefingPeriod.Week, from: "2026-08-10", to: "2026-08-17")));
        Assert.Contains("Luna trecută, iulie 2026",
            BriefingComposer.Compose(Data(kind: BriefingPeriod.Month, from: "2026-07-01", to: "2026-08-01")));
    }

    [Fact]
    public void SaptamanaPesteGranitaDeLuna_ScrieAmbeleLuni()
    {
        var text = BriefingComposer.Compose(
            Data(kind: BriefingPeriod.Week, from: "2026-07-27", to: "2026-08-03"));

        Assert.Contains("27 iulie – 2 august", text);
    }

    [Fact]
    public void TelefonLipsa_SeSpune_DarNumaiPeSaptamanaSiLuna()
    {
        // pe zi tăcerea e corectă: Screen Time se importă pe săptămâni, nu zilnic
        Assert.DoesNotContain("importă", BriefingComposer.Compose(Data(phoneMissing: true)));

        Assert.Contains("importă Screen Time",
            BriefingComposer.Compose(Data(kind: BriefingPeriod.Week, phoneMissing: true)));
        Assert.Contains("importă Screen Time",
            BriefingComposer.Compose(Data(kind: BriefingPeriod.Month, phoneMissing: true)));
    }

    [Fact]
    public void TelefonPrezent_NuMaiCereImport()
    {
        Assert.DoesNotContain("importă", BriefingComposer.Compose(
            Data(kind: BriefingPeriod.Week, phoneMin: 200, phoneMissing: false)));
    }

    [Fact]
    public void DataEsteInRomana_IndiferentDeCulturaMasinii()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Contains("august", BriefingComposer.Compose(Data()));
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void FromReport_CitesteRaportulRealSiTopurilePeClasa()
    {
        var day = new
        {
            totals = new
            {
                activeSeconds = 22320.0,
                byClass = new Dictionary<string, double>
                {
                    ["productive"] = 14700, ["neutral"] = 4200, ["unproductive"] = 3420,
                },
            },
            classDetail = new Dictionary<string, object>
            {
                ["productive"] = new
                {
                    apps = new[] { new { name = "Code.exe", seconds = 9660.0 } },
                    domains = new[] { new { name = "github.example", seconds = 2400.0 } },
                },
                ["unproductive"] = new
                {
                    apps = new[] { new { name = "Chat.exe", seconds = 1800.0 } },
                    domains = new[] { new { name = "memebox.example", seconds = 3600.0 } },
                },
            },
            phone = new
            {
                totalMinutes = 204.0,
                periods = new[] { new { From = "2026-08-10" } },
                apps = new[]
                {
                    new { name = "MemeBox", minutes = 90.0, cls = "unproductive" },
                    new { name = "Carte", minutes = 40.0, cls = "productive" },
                    new { name = "Harta", minutes = 30.0, cls = "neutral" },
                },
            },
        };
        var week = new { totals = new { activeSeconds = 146160.0 } };

        var d = BriefingComposer.FromReport(
            BriefingPeriod.Day, new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 15), day, week, 7);

        Assert.Equal(22320, d.ActiveSeconds);
        Assert.Equal(204, d.PhoneMinutes);
        Assert.Equal(146160.0 / 7, d.CompareSeconds);
        Assert.False(d.PhoneMissing);

        // aplicatiile si site-urile clasei se amesteca si se sorteaza dupa timp
        Assert.Equal("Code.exe", d.TopProductive![0].Name);
        Assert.Equal("memebox.example", d.TopUnproductive![0].Name);
        Assert.Equal("Chat.exe", d.TopUnproductive[1].Name);

        // telefonul se filtreaza dupa clasa proprie, nu dupa timpul total
        Assert.Equal("MemeBox", d.PhoneTopUnproductive![0].Name);
        Assert.Equal("Carte", d.PhoneTopProductive![0].Name);
        Assert.DoesNotContain(d.PhoneTopProductive, i => i.Name == "Harta"); // neutru nu intra nicaieri
    }

    [Fact]
    public void FromReport_BrowserulNuIntraInClasament()
    {
        // un browser e recipient: timpul lui e deja detaliat de site-urile din el, care apar
        // separat in aceeasi lista. Numarat, ar arata „msedge.exe" acolo unde raspunsul util
        // e site-ul propriu-zis.
        var rep = new
        {
            classDetail = new Dictionary<string, object>
            {
                ["unproductive"] = new
                {
                    apps = new[] { new { name = "msedge.exe", seconds = 7000.0 } },
                    domains = new[] { new { name = "memebox.example", seconds = 3400.0 } },
                },
            },
        };

        var d = BriefingComposer.FromReport(
            BriefingPeriod.Week, new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 17), rep);

        Assert.Single(d.TopUnproductive!);
        Assert.Equal("memebox.example", d.TopUnproductive![0].Name);
    }

    [Fact]
    public void FromReport_FaraPerioadeDeTelefon_MarcheazaLipsa()
    {
        // „lipsa" inseamna ca nu exista niciun import care atinge intervalul — nu ca e zero.
        // Zero minute importate e o informatie, niciun import e alta.
        var rep = new { phone = new { totalMinutes = 0.0, periods = Array.Empty<object>() } };

        var d = BriefingComposer.FromReport(
            BriefingPeriod.Week, new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 17), rep);

        Assert.True(d.PhoneMissing);
    }

    [Fact]
    public void FromReport_RaportGol_NuArunca()
    {
        var d = BriefingComposer.FromReport(
            BriefingPeriod.Day, new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 15), new { }, null);

        Assert.Equal(0, d.ActiveSeconds);
        Assert.Equal(0, d.PhoneMinutes);
        Assert.Empty(d.TopProductive!);
        Assert.Empty(d.TopUnproductive!);
        Assert.Equal(0, d.CompareSeconds);
    }

    [Fact]
    public void FromReport_ActivitatiSubUnMinut_SuntIgnorate()
    {
        var rep = new
        {
            classDetail = new Dictionary<string, object>
            {
                ["productive"] = new
                {
                    apps = new[]
                    {
                        new { name = "fulger.exe", seconds = 30.0 },
                        new { name = "Code.exe", seconds = 600.0 },
                    },
                },
            },
        };

        var d = BriefingComposer.FromReport(
            BriefingPeriod.Day, new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 15), rep);

        Assert.Single(d.TopProductive!);
        Assert.Equal("Code.exe", d.TopProductive![0].Name);
    }
}
