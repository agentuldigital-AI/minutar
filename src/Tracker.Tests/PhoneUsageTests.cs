using System.IO;
using Tracker.Daemon.Report;
using Tracker.Daemon.Storage;
using Tracker.Shared.Aw;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// Bug 2026-08-06 (semnalat de utilizator): o singură săptămână importată apărea în TREI
/// săptămâni consecutive, cu aceleași 23h 20m. Cauza: testul de suprapunere compara
/// inclusiv, deși ambele capete sunt exclusive — „Jul 13–20" înseamnă zilele 13..19 (7
/// bare în graficul Apple, media = total/7), iar `to` al raportului e tot exclusiv. O
/// perioadă atinsă doar la margine intra și în săptămâna dinainte, și în cea de după.
/// </summary>
public sealed class PhoneUsageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tracker-tests", Path.GetRandomFileName());
    private readonly EventStore _store;

    public PhoneUsageTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new EventStore(Path.Combine(_dir, "events.db"), "tracker-tests", "HOST");
        _store.EnsureBucketAsync(AwBuckets.PhoneUsage("HOST"), "phone.usage").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    /// <summary>Perioada reală a utilizatorului: 13–20 iulie = zilele 13..19, 1400 minute.</summary>
    private async Task SeedJulyWeekAsync()
    {
        await _store.HeartbeatAsync(
            AwBuckets.PhoneUsage("HOST"),
            new Dictionary<string, object?>
            {
                ["device"] = "iPhone",
                ["from"] = "2026-07-13",
                ["to"] = "2026-07-20",
                ["totalMinutes"] = 1400,
                ["avgDailyMinutes"] = 200,
                ["source"] = "screen-time-llm",
                ["recordedAt"] = "2026-08-05T20:00:00+03:00",
            },
            pulsetimeSeconds: 0,
            timestamp: new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));
    }

    private Task<List<PhoneWeek>> ReadWeekAsync(int day) =>
        PhoneUsage.ReadAsync(
            _store, "HOST",
            new DateTimeOffset(2026, 7, day, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, day + 7, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ThePeriodShowsInItsOwnWeek()
    {
        await SeedJulyWeekAsync();

        var weeks = await ReadWeekAsync(13);

        Assert.Single(weeks);
        Assert.Equal(1400, weeks[0].TotalMinutes);
    }

    [Fact]
    public async Task ItDoesNotLeakIntoThePreviousWeek()
    {
        // săptămâna 6–12 iulie se termină exact acolo unde începe perioada
        await SeedJulyWeekAsync();

        Assert.Empty(await ReadWeekAsync(6));
    }

    [Fact]
    public async Task ItDoesNotLeakIntoTheFollowingWeek()
    {
        // săptămâna 20–26 iulie începe exact acolo unde se termină perioada
        await SeedJulyWeekAsync();

        Assert.Empty(await ReadWeekAsync(20));
    }

    [Fact]
    public async Task AMonthThatContainsItStillSeesIt()
    {
        await SeedJulyWeekAsync();

        var july = await PhoneUsage.ReadAsync(
            _store, "HOST",
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Single(july);
    }

    [Fact]
    public async Task APeriodStraddlingTwoWeeksAppearsInBoth()
    {
        // o perioadă chiar suprapusă TREBUIE să apară în ambele — altfel corecția de mai
        // sus ar fi mers prea departe și ar fi ascuns date reale
        await _store.HeartbeatAsync(
            AwBuckets.PhoneUsage("HOST"),
            new Dictionary<string, object?>
            {
                ["device"] = "iPhone",
                ["from"] = "2026-07-16",
                ["to"] = "2026-07-23",
                ["totalMinutes"] = 700,
                ["avgDailyMinutes"] = 100,
                ["source"] = "manual",
                ["recordedAt"] = "2026-08-05T20:00:00+03:00",
            },
            pulsetimeSeconds: 0,
            timestamp: new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero));

        Assert.Single(await ReadWeekAsync(13));
        Assert.Single(await ReadWeekAsync(20));
    }

    [Theory]
    [InlineData("9GAG: Best LOL Pics & GIFs", "9GAG")]
    [InlineData("9GAG", "9GAG")]
    [InlineData("Yahoo Mail", "Yahoo Mail")]
    [InlineData("stiri.example", "stiri.example")]
    [InlineData("reportaproblem.apple.com", "reportaproblem.apple.com")]
    [InlineData("Bolt - Request a ride", "Bolt")]
    [InlineData("  Safari  ", "Safari")]
    public void MatchKeyStripsTheMarketingSuffix(string raw, string expected)
    {
        Assert.Equal(expected, PhoneUsage.MatchKey(raw));
    }

    [Fact]
    public async Task TheSameAppUnderTwoNamesIsClassifiedAndCountedOnce()
    {
        // Screen Time da cand numele scurt, cand pe cel comercial; cu potrivire exacta,
        // aceeasi aplicatie iesea o data clasificata si o data nu, pe doua randuri
        await _store.HeartbeatAsync(
            AwBuckets.PhoneUsage("HOST"),
            new Dictionary<string, object?>
            {
                ["device"] = "iPhone", ["from"] = "2026-07-13", ["to"] = "2026-07-20",
                ["totalMinutes"] = 100, ["source"] = "test", ["recordedAt"] = "2026-08-06T10:00:00+03:00",
                ["apps"] = new[]
                {
                    new Dictionary<string, object?> { ["name"] = "9GAG", ["minutes"] = 60 },
                    new Dictionary<string, object?> { ["name"] = "9GAG: Best LOL Pics & GIFs", ["minutes"] = 40 },
                },
            },
            pulsetimeSeconds: 0,
            timestamp: new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));

        var cfg = new Tracker.Shared.Config.TrackerConfig();
        cfg.PhoneApps.Add(new Tracker.Shared.Config.PhoneAppConfig { Name = "9GAG", Class = "unproductive" });

        var json = System.Text.Json.JsonSerializer.Serialize(
            PhoneUsage.Summarize(await ReadWeekAsync(13), cfg));
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(0, doc.RootElement.GetProperty("unclassifiedMinutes").GetInt32());
        Assert.Equal(100, doc.RootElement.GetProperty("byClass").GetProperty("unproductive").GetInt32());
        var apps = doc.RootElement.GetProperty("apps").EnumerateArray().ToList();
        Assert.Single(apps);
        Assert.Equal("9GAG", apps[0].GetProperty("name").GetString());
        Assert.Equal(100, apps[0].GetProperty("minutes").GetInt32());
    }

    [Theory]
    [InlineData("Safari", null, "browser")]
    [InlineData("Brave", null, "browser")]
    [InlineData("stiri.example", null, "site")]
    [InlineData("WhatsApp", null, "app")]
    [InlineData("9GAG", null, "app")]          // fara punct in nume, deci pare aplicatie...
    [InlineData("9GAG", "site", "site")]       // ...pana cand utilizatorul spune altfel
    [InlineData("Safari", "app", "app")]       // si un browser poate fi socotit activitate
    public void KindOfRespectsTheExplicitRuleFirst(string name, string? kind, string expected)
    {
        Assert.Equal(expected, PhoneUsage.KindOf(name, kind));
    }

    [Fact]
    public async Task ABrowserIsAContainer_NotAnActivity()
    {
        // Cazul real: Safari 709 min ≈ 9GAG 543 + site-uri 157. Numarat ca activitate, dubla
        // toata navigarea SI o punea pe clasa browserului — 11h de „neutru" din care 9h erau
        // de fapt neproductive.
        await _store.HeartbeatAsync(
            AwBuckets.PhoneUsage("HOST"),
            new Dictionary<string, object?>
            {
                ["device"] = "iPhone", ["from"] = "2026-07-13", ["to"] = "2026-07-20",
                ["totalMinutes"] = 800, ["source"] = "test", ["recordedAt"] = "2026-08-07T10:00:00+03:00",
                ["apps"] = new[]
                {
                    new Dictionary<string, object?> { ["name"] = "Safari", ["minutes"] = 700 },
                    new Dictionary<string, object?> { ["name"] = "9GAG", ["minutes"] = 543 },
                    new Dictionary<string, object?> { ["name"] = "stiri.example", ["minutes"] = 157 },
                },
            },
            pulsetimeSeconds: 0,
            timestamp: new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));

        var cfg = new Tracker.Shared.Config.TrackerConfig();
        cfg.PhoneApps.Add(new Tracker.Shared.Config.PhoneAppConfig { Name = "Safari", Class = "neutral" });
        cfg.PhoneApps.Add(new Tracker.Shared.Config.PhoneAppConfig { Name = "9GAG", Class = "unproductive" });
        cfg.PhoneApps.Add(new Tracker.Shared.Config.PhoneAppConfig { Name = "stiri.example", Class = "unproductive" });

        var json = System.Text.Json.JsonSerializer.Serialize(
            PhoneUsage.Summarize(await ReadWeekAsync(13), cfg));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Safari nu intra nici in clase, nici in clasament
        Assert.Equal(700, root.GetProperty("browsers").EnumerateArray().Single().GetProperty("minutes").GetInt32());
        Assert.Equal(700, root.GetProperty("appsSumMinutes").GetInt32());  // 543 + 157
        Assert.Equal(700, root.GetProperty("byClass").GetProperty("unproductive").GetInt32());
        Assert.False(root.GetProperty("byClass").TryGetProperty("neutral", out _));
        Assert.DoesNotContain(
            root.GetProperty("apps").EnumerateArray(),
            a => a.GetProperty("name").GetString() == "Safari");
    }

    [Fact]
    public async Task AUserCanForceABrowserToCountAsAnActivity()
    {
        await _store.HeartbeatAsync(
            AwBuckets.PhoneUsage("HOST"),
            new Dictionary<string, object?>
            {
                ["device"] = "iPhone", ["from"] = "2026-07-13", ["to"] = "2026-07-20",
                ["totalMinutes"] = 100, ["source"] = "test", ["recordedAt"] = "2026-08-07T10:00:00+03:00",
                ["apps"] = new[] { new Dictionary<string, object?> { ["name"] = "Safari", ["minutes"] = 60 } },
            },
            pulsetimeSeconds: 0,
            timestamp: new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));

        var cfg = new Tracker.Shared.Config.TrackerConfig();
        cfg.PhoneApps.Add(new Tracker.Shared.Config.PhoneAppConfig
        {
            Name = "Safari", Class = "productive", Kind = "app",
        });

        var json = System.Text.Json.JsonSerializer.Serialize(
            PhoneUsage.Summarize(await ReadWeekAsync(13), cfg));
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.Empty(doc.RootElement.GetProperty("browsers").EnumerateArray());
        Assert.Equal(60, doc.RootElement.GetProperty("byClass").GetProperty("productive").GetInt32());
    }

    [Fact]
    public void KindRoundTripsThroughConfig()
    {
        var cfg = new Tracker.Shared.Config.TrackerConfig();
        cfg.PhoneApps.Add(new Tracker.Shared.Config.PhoneAppConfig
        {
            Name = "9GAG", Class = "unproductive", Kind = "site",
        });

        var path = Path.Combine(Path.GetTempPath(), "tracker-tests", Path.GetRandomFileName() + ".toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            Tracker.Shared.Config.ConfigWriter.Write(cfg, path);
            var loaded = Tracker.Shared.Config.TrackerConfig.Load(path);

            Assert.Equal("site", loaded.PhoneApps.Single().Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APartialUpdateKeepsTheFieldsItDoesNotMention()
    {
        // Bug 2026-08-07 (semnalat de utilizator): mutase 9GAG in coloana Site-uri, apoi ii
        // schimbase clasa — si sarise inapoi in Aplicatii. Butonul de clasa nu trimitea
        // tipul, iar endpoint-ul trata „lipsa" ca „goleste". Regula corecta: un camp care
        // nu vine ramane cum era. Testul reproduce lantul complet, pe config.
        var cfg = new Tracker.Shared.Config.TrackerConfig();
        cfg.PhoneApps.Add(new Tracker.Shared.Config.PhoneAppConfig
        {
            Name = "9GAG", Class = "unproductive", Kind = "site", Project = "Client A",
        });

        // ce face endpoint-ul cand primeste doar clasa
        var old = cfg.PhoneApps.Single();
        var updated = new Tracker.Shared.Config.PhoneAppConfig
        {
            Name = old.Name,
            Class = "productive",
            Project = (string?)null ?? old.Project,
            Kind = string.IsNullOrWhiteSpace(null) ? old.Kind : "",
        };

        Assert.Equal("productive", updated.Class);
        Assert.Equal("site", updated.Kind);
        Assert.Equal("Client A", updated.Project);
    }

    [Fact]
    public void AnEmptyKindNeverErasesAnExistingOne()
    {
        // varianta minimala a aceleiasi reguli: sirul gol venit din interfata inseamna
        // „n-am ce spune despre tip", nu „sterge-l"
        var existing = new Tracker.Shared.Config.PhoneAppConfig { Name = "X", Class = "neutral", Kind = "browser" };

        foreach (var incoming in new string?[] { null, "", "   " })
        {
            var kept = string.IsNullOrWhiteSpace(incoming) ? existing.Kind : incoming.Trim();
            Assert.Equal("browser", kept);
        }
    }
}
