using System.Globalization;
using System.IO;
using Tracker.Daemon.Briefing;
using Tracker.Shared.Config;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// ConfigWriter.Serialize scrie MANUAL fiecare secțiune, iar șase endpointuri rescriu configul în
/// timpul rulării (salvare setări, clasificare telefon, alocări). O secțiune uitată acolo dispare
/// tăcut la prima salvare din dashboard — adică token-ul de Telegram s-ar șterge singur.
/// Testul ăsta e paznicul acelei capcane.
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
        cfg.Telegram.BriefingDelaySeconds = 40;

        ConfigWriter.Write(cfg, path);
        var reloaded = TrackerConfig.Load(path);

        Assert.True(reloaded.Telegram.Enabled);
        Assert.Equal("123456:ABC-DEF_ghi", reloaded.Telegram.BotToken);
        Assert.Equal("987654321", reloaded.Telegram.ChatId);
        Assert.False(reloaded.Telegram.DailyBriefing);
        Assert.Equal(40, reloaded.Telegram.BriefingDelaySeconds);
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
/// Briefingul de dimineață. Compunerea e pură (cifre → text), deci se poate testa fără rețea și
/// fără daemon — exact motivul pentru care e separată de trimitere.
///
/// Regulile pe care le apără testele astea:
///  - o zi goală nu produce un mesaj cu „0m" peste tot, ci o propoziție care spune asta;
///  - numele de aplicații ajung în HTML, deci trebuie escapate (un „AT&amp;T" strica mesajul);
///  - raportul vine ca tip anonim serializat, iar câmpurile lipsă nu au voie să arunce.
/// </summary>
public class BriefingComposerTests
{
    private static BriefingData Data(
        double active = 6 * 3600 + 12 * 60,
        double prod = 4 * 3600,
        double neutral = 3600,
        double unprod = 72 * 60,
        double phoneMin = 0,
        double weekAvg = 0,
        IReadOnlyList<(string, double)>? apps = null) =>
        new(new DateOnly(2026, 8, 14), active, prod, neutral, unprod, phoneMin,
            apps ?? Array.Empty<(string, double)>(), weekAvg);

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
        // 4h din 8h = 50%, nu 50% din altceva
        var text = BriefingComposer.Compose(Data(active: 8 * 3600, prod: 4 * 3600, neutral: 2 * 3600, unprod: 2 * 3600));

        Assert.Contains("Productiv 4h 00m (50%)", text);
        Assert.Contains("Neutru 2h 00m (25%)", text);
    }

    [Fact]
    public void ZiComplectGoala_SpuneAsta_NuInsiraZerouri()
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
    public void FaraDateDeTelefon_LiniaLipseste()
    {
        Assert.DoesNotContain("Telefon", BriefingComposer.Compose(Data(phoneMin: 0)));
    }

    [Fact]
    public void MediaPeSaptamana_ApareDoarCandExista()
    {
        Assert.DoesNotContain("media", BriefingComposer.Compose(Data(weekAvg: 0)));
        Assert.Contains("media ultimelor 7 zile: 5h 48m", BriefingComposer.Compose(Data(weekAvg: 5 * 3600 + 48 * 60)));
    }

    [Fact]
    public void ClaseleSubUnMinut_NuIntraInMesaj()
    {
        // altfel apărea „Neproductiv 0m (0%)”, care e zgomot, nu informație
        var text = BriefingComposer.Compose(Data(active: 3600, prod: 3600, neutral: 0, unprod: 20));

        Assert.Contains("Productiv", text);
        Assert.DoesNotContain("Neutru", text);
        Assert.DoesNotContain("Neproductiv", text);
    }

    [Fact]
    public void NumeleDeAplicatii_SuntEscapateCaHtml()
    {
        var text = BriefingComposer.Compose(Data(apps: new[] { ("AT&T <Research>", 3600d) }));

        Assert.Contains("AT&amp;T &lt;Research&gt;", text);
        Assert.DoesNotContain("<Research>", text);
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
    public void FromReport_CitesteRaportulRealSiCalculeazaMedia()
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
            byApp = new[]
            {
                new { name = "chrome.exe", seconds = 9660.0 },
                new { name = "Code.exe", seconds = 6720.0 },
                new { name = "slack.exe", seconds = 2640.0 },
                new { name = "explorer.exe", seconds = 600.0 },
            },
            phone = new { totalMinutes = 204.0 },
        };
        var week = new { totals = new { activeSeconds = 146160.0 } };

        var d = BriefingComposer.FromReport(new DateOnly(2026, 8, 14), day, week);

        Assert.Equal(22320, d.ActiveSeconds);
        Assert.Equal(14700, d.ProductiveSeconds);
        Assert.Equal(204, d.PhoneMinutes);
        Assert.Equal(3, d.TopApps.Count); // doar primele trei intră în mesaj
        Assert.Equal("chrome.exe", d.TopApps[0].Name);
        Assert.Equal(146160.0 / 7, d.WeekAverageSeconds);
    }

    [Fact]
    public void FromReport_RaportGol_NuArunca()
    {
        var d = BriefingComposer.FromReport(new DateOnly(2026, 8, 14), new { }, null);

        Assert.Equal(0, d.ActiveSeconds);
        Assert.Equal(0, d.PhoneMinutes);
        Assert.Empty(d.TopApps);
        Assert.Equal(0, d.WeekAverageSeconds);
    }

    [Fact]
    public void FromReport_AplicatiiFaraTimp_SuntIgnorate()
    {
        var day = new { byApp = new[] { new { name = "idle.exe", seconds = 0.0 }, new { name = "chrome.exe", seconds = 60.0 } } };

        var d = BriefingComposer.FromReport(new DateOnly(2026, 8, 14), day, null);

        Assert.Single(d.TopApps);
        Assert.Equal("chrome.exe", d.TopApps[0].Name);
    }
}
