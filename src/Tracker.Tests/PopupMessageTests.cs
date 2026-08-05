using Tracker.Daemon.Popup;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// The warning popup rotates its opening line (user decision, 2026-08-04). What must hold:
/// a line is always produced, it never repeats back-to-back, every placeholder is filled,
/// and the tone stays warm — no accusations, which is why the wording lives in one place.
/// </summary>
public sealed class PopupMessageTests
{
    private static PopupMessageContext Ctx(
        int minutes = 5, string site = "YouTube", int times = 1, int totalSeconds = 300,
        string? priority = null, int hour = 14) =>
        new(minutes, site, new DateTimeOffset(2026, 8, 4, hour, 30, 0, TimeSpan.FromHours(3)),
            times, totalSeconds, priority);

    [Fact]
    public void AlwaysProducesANonEmptyLine()
    {
        var picker = new PopupMessagePicker(seed: 1);

        for (var i = 0; i < 200; i++)
            Assert.False(string.IsNullOrWhiteSpace(picker.Pick(Ctx(minutes: i % 60, times: i % 5 + 1))));
    }

    [Fact]
    public void NeverRepeatsTheSameLineTwiceInARow()
    {
        var picker = new PopupMessagePicker(seed: 7);
        string? prev = null;

        for (var i = 0; i < 300; i++)
        {
            var text = picker.Pick(Ctx(minutes: 25, times: 3, priority: "raport", hour: 22));
            Assert.NotEqual(prev, text);
            prev = text;
        }
    }

    [Fact]
    public void LeavesNoPlaceholderUnfilled()
    {
        var picker = new PopupMessagePicker(seed: 3);

        for (var i = 0; i < 300; i++)
        {
            var text = picker.Pick(Ctx(minutes: 30, times: 4, priority: "termină oferta", hour: 9));
            Assert.DoesNotContain("{", text);
            Assert.DoesNotContain("}", text);
        }
    }

    [Fact]
    public void PriorityLinesOnlyAppearWhenThereIsOne()
    {
        var picker = new PopupMessagePicker(seed: 11);

        for (var i = 0; i < 300; i++)
        {
            var text = picker.Pick(Ctx(priority: null));
            Assert.DoesNotContain("Ți-ai propus", text);
            Assert.DoesNotContain("Top 3", text);
        }
    }

    [Fact]
    public void ToneStaysWarm_NoAccusations()
    {
        var picker = new PopupMessagePicker(seed: 5);
        // wording we deliberately keep out: scolding, "you should", "again you…"
        string[] banned = { "ar trebui", "iar te-ai", "pierzi vremea", "prostii", "rușine", "leneș" };

        for (var i = 0; i < 400; i++)
        {
            var text = picker.Pick(Ctx(minutes: i % 45, times: i % 6 + 1, priority: i % 2 == 0 ? "x" : null, hour: i % 24));
            foreach (var bad in banned)
                Assert.DoesNotContain(bad, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("domain:youtube.com", "chrome.exe", "YouTube")]
    [InlineData("domain:www.facebook.com", "chrome.exe", "Facebook")]
    [InlineData("domain:9gag.com", "chrome.exe", "9GAG")]
    [InlineData("app:WhatsApp.Root.exe", "WhatsApp.Root.exe", "WhatsApp.Root")]
    [InlineData(null, "Solitaire.exe", "Solitaire")]
    public void SiteNameIsReadable(string? rule, string app, string expected)
    {
        Assert.Equal(expected, PopupMessagePicker.SiteNameOf(rule, app));
    }

    [Fact]
    public void DismissMinutes_RoundTripsThroughConfig()
    {
        // the X's quiet period is configurable; it must survive a save/load cycle, or a
        // settings save would silently reset it to the default
        var cfg = new Tracker.Shared.Config.TrackerConfig();
        cfg.Popup.DismissMinutes = 3;

        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tracker-tests", System.IO.Path.GetRandomFileName() + ".toml");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try
        {
            Tracker.Shared.Config.ConfigWriter.Write(cfg, path);
            var loaded = Tracker.Shared.Config.TrackerConfig.Load(path);

            Assert.Equal(3, loaded.Popup.DismissMinutes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void DismissMinutes_HasASaneDefault()
    {
        // zero would make the X pointless: the popup returns on the next tick
        var cfg = new Tracker.Shared.Config.TrackerConfig();

        Assert.True(cfg.Popup.DismissMinutes >= 1);
        Assert.True(cfg.Popup.DismissMinutes < cfg.Popup.RenagMinutesDefault,
            "închiderea trebuie să promită MAI PUȚIN decât ignorarea popup-ului");
    }

    [Fact]
    public void PhoneApps_RoundTripThroughConfig()
    {
        // clasificarea aplicațiilor de telefon se salvează în tracker.toml; dacă nu
        // supraviețuiește unui save/load, utilizatorul ar reclasifica la fiecare import
        var cfg = new Tracker.Shared.Config.TrackerConfig();
        cfg.PhoneApps.Add(new Tracker.Shared.Config.PhoneAppConfig
        {
            Name = "9GAG", Class = "unproductive",
        });
        cfg.PhoneApps.Add(new Tracker.Shared.Config.PhoneAppConfig
        {
            Name = "Slack", Class = "productive", Project = "Client A",
        });

        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tracker-tests", System.IO.Path.GetRandomFileName() + ".toml");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try
        {
            Tracker.Shared.Config.ConfigWriter.Write(cfg, path);
            var loaded = Tracker.Shared.Config.TrackerConfig.Load(path);

            Assert.Equal(2, loaded.PhoneApps.Count);
            var gag = loaded.PhoneApps.Single(p => p.Name == "9GAG");
            Assert.Equal("unproductive", gag.Class);
            Assert.Equal("", gag.Project);
            var slack = loaded.PhoneApps.Single(p => p.Name == "Slack");
            Assert.Equal("productive", slack.Class);
            Assert.Equal("Client A", slack.Project);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void PhoneApps_RejectAnInvalidClass()
    {
        var cfg = new Tracker.Shared.Config.TrackerConfig();
        cfg.PhoneApps.Add(new Tracker.Shared.Config.PhoneAppConfig { Name = "X", Class = "foarte-productiv" });

        Assert.Throws<System.IO.InvalidDataException>(() => cfg.Validate());
    }

    [Fact]
    public void UsesEveryLineOfAGroupBeforeRepeatingIt()
    {
        // with only "opening" eligible (short streak, midday, no priority, first time),
        // its four lines must all appear before any comes round again
        var picker = new PopupMessagePicker(seed: 2);
        var seen = new HashSet<string>();

        for (var i = 0; i < 4; i++) seen.Add(picker.Pick(Ctx(minutes: 3, times: 1, hour: 14)));

        Assert.Equal(4, seen.Count);
    }
}
