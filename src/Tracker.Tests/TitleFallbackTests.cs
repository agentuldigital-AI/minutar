using Tracker.Daemon.Engine;
using Tracker.Shared.Config;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// Guards the title fallback used when no URL is available (browser without the extension).
/// Three real mis-attributions from 2026-08-04 are pinned here: a project domain leaking
/// into every app through its subdomain prefix, browser-internal pseudo-domains matching
/// titles, and a profile fragment matching as a substring.
/// </summary>
public sealed class TitleFallbackTests
{
    private static TrackerConfig Config()
    {
        var cfg = new TrackerConfig();
        cfg.Classification.Default = "neutral";
        cfg.Browser.Processes = new List<string> { "chrome.exe", "msedge.exe", "brave.exe" };
        cfg.Classification.Rules.Add(new ClassificationRule { Class = "productive", Match = "domain", Value = "settings" });
        cfg.Classification.Rules.Add(new ClassificationRule { Class = "productive", Match = "domain", Value = "github.com" });
        cfg.Classification.Rules.Add(new ClassificationRule { Class = "unproductive", Match = "domain", Value = "youtube.com" });
        cfg.Projects.Add(new ProjectConfig
        {
            Name = "Client Zoho",
            Domains = new List<string> { "mail.zoho.eu" },
        });
        cfg.Projects.Add(new ProjectConfig
        {
            Name = "general",
            BrowserProfiles = new List<string> { "General" },
        });
        return cfg;
    }

    [Theory]
    // a subdomain must NOT collapse to its prefix: "mail.zoho.eu" is not "mail"
    [InlineData("chrome.exe", "Mail - Iustin - Outlook")]
    // …and a project domain must not reach non-browser windows at all
    [InlineData("olk.exe", "Mail")]
    [InlineData("explorer.exe", "mail atasamente")]
    public void ProjectDomain_DoesNotMatchTitleThroughSubdomainPrefix(string app, string title)
    {
        var project = new AttributionEngine().Attribute(
            Config(), app, title, aumid: "", url: null, profileLabel: null, DateTimeOffset.UtcNow);

        Assert.Null(project);
    }

    [Fact]
    public void ProjectDomain_StillMatchesOnRealUrl()
    {
        var project = new AttributionEngine().Attribute(
            Config(), "chrome.exe", "Inbox", aumid: "", url: "https://mail.zoho.eu/inbox",
            profileLabel: null, DateTimeOffset.UtcNow);

        Assert.Equal("Client Zoho", project);
    }

    [Theory]
    [InlineData("Settings - Brave")]
    [InlineData("Downloads")]
    public void BrowserInternalPseudoDomain_DoesNotMatchTitle(string title)
    {
        var cls = new ClassificationEngine().Classify(Config(), "brave.exe", title, null, null);

        Assert.Equal("neutral", cls.Class);
    }

    [Fact]
    public void BrowserInternalPseudoDomain_StillMatchesItsOwnUrl()
    {
        // chrome://settings parses with Host="settings", so the rule keeps working where it was meant to
        var cls = new ClassificationEngine().Classify(Config(), "brave.exe", "Settings", "chrome://settings", null);

        Assert.Equal("productive", cls.Class);
    }

    [Theory]
    [InlineData("github.com hosting - Chrome", "productive")]
    [InlineData("Ceva - YouTube", "unproductive")]
    public void SecondLevelDomain_StillMatchesTitle(string title, string expected)
    {
        var cls = new ClassificationEngine().Classify(Config(), "chrome.exe", title, null, null);

        Assert.Equal(expected, cls.Class);
    }

    [Theory]
    [InlineData("chrome.exe", "Setari generale - Panou")]
    [InlineData("chrome.exe", "General Motors - stiri")]
    // Edge is the only browser that puts the profile name in the title, and only as a word
    [InlineData("msedge.exe", "Raport generale - Microsoft Edge")]
    public void ProfileFragment_DoesNotMatchTitleAsSubstring(string app, string title)
    {
        var project = new AttributionEngine().Attribute(
            Config(), app, title, aumid: "", url: null, profileLabel: null, DateTimeOffset.UtcNow);

        Assert.Null(project);
    }

    [Theory]
    // only the last-but-one segment of an Edge title names the profile
    [InlineData("Ceva - General - Microsoft Edge")]
    [InlineData("Raport lunar - General Work - Microsoft Edge")]
    // Edge writes a zero-width space inside "Microsoft Edge" — real events show it
    [InlineData("Ceva - General - Microsoft​ Edge")]
    public void ProfileFragment_MatchesTheProfileSegmentOfAnEdgeTitle(string title)
    {
        var project = new AttributionEngine().Attribute(
            Config(), "msedge.exe", title, aumid: "", url: null,
            profileLabel: null, DateTimeOffset.UtcNow);

        Assert.Equal("general", project);
    }

    [Theory]
    // the profile name must not be found in the PAGE name (files named after a client)
    [InlineData("CLIENTC_DECK_VANZARE - Profile 1 - Microsoft Edge")]
    [InlineData("General Motors - Profile 1 - Microsoft Edge")]
    // …nor when the title is not an Edge window title at all
    [InlineData("General")]
    [InlineData("Ceva - General")]
    public void ProfileFragment_IgnoresEverythingOutsideTheProfileSegment(string title)
    {
        var cfg = Config();
        cfg.Projects.Add(new ProjectConfig { Name = "Client C", BrowserProfiles = new List<string> { "Client C" } });

        var project = new AttributionEngine().Attribute(
            cfg, "msedge.exe", title, aumid: "", url: null, profileLabel: null, DateTimeOffset.UtcNow);

        Assert.Null(project);
    }

    [Theory]
    // A profile fragment must not be found inside an unrelated document title. Client work
    // is usually named after the client, so "Contains" made every such window that client's
    // — in the report only, because the live engine had already been fixed (2026-08-04).
    [InlineData("chrome.exe", "CLIENTC_DECK_VANZARE - Google Chrome")]
    [InlineData("chrome.exe", "General Motors - stiri")]
    [InlineData("msedge.exe", "Raport generale - Microsoft Edge")]
    public void ReportAndLiveAgree_OnProfileFragmentsInsideTitles(string app, string title)
    {
        var cfg = Config();
        cfg.Projects.Add(new ProjectConfig { Name = "Client C", BrowserProfiles = new List<string> { "Client C" } });

        var live = new AttributionEngine().Attribute(
            cfg, app, title, aumid: "", url: null, profileLabel: null, DateTimeOffset.UtcNow);
        var report = AttributionEngine.Resolve(cfg, app, title, aumid: "", url: null, profileLabel: null);

        Assert.Null(report);
        Assert.Equal(report, live);
    }

    [Fact]
    public void ReportAndLiveAgree_WhenTheExtensionLabelIdentifiesTheProfile()
    {
        var cfg = Config();
        cfg.Projects.Add(new ProjectConfig { Name = "Client C", BrowserProfiles = new List<string> { "Client C" } });

        var report = AttributionEngine.Resolve(
            cfg, "chrome.exe", "CLIENTC_DECK_VANZARE", aumid: "", url: null, profileLabel: "Client C");
        var live = new AttributionEngine().Attribute(
            cfg, "chrome.exe", "CLIENTC_DECK_VANZARE", aumid: "", url: null, profileLabel: "Client C",
            DateTimeOffset.UtcNow);

        Assert.Equal("Client C", report);
        Assert.Equal(report, live);
    }

    [Fact]
    public void ReportAndLiveAgree_OnAnExplicitKeyword()
    {
        // the deliberate way to claim documents by name
        var cfg = Config();
        cfg.Projects.Add(new ProjectConfig { Name = "Client C", Keywords = new List<string> { "clientc" } });

        var report = AttributionEngine.Resolve(
            cfg, "chrome.exe", "CLIENTC_DECK_VANZARE - Google Chrome", aumid: "", url: null, profileLabel: null);

        Assert.Equal("Client C", report);
    }

    [Fact]
    public void Hold_DoesNotCarryTheProjectIntoAnotherBrowserProfile()
    {
        var cfg = Config();
        var att = new AttributionEngine();
        var t0 = DateTimeOffset.UtcNow;

        // profile WITH the extension: attributed from its label
        Assert.Equal("general", att.Attribute(
            cfg, "chrome.exe", "Calendly", "Chrome.UserData.Profile2", null, "General", t0));

        // switch to a profile WITHOUT it — same chrome.exe, different window identity
        Assert.Null(att.Attribute(
            cfg, "chrome.exe", "New Tab", "Chrome", null, null, t0.AddSeconds(3)));
    }

    [Fact]
    public void Hold_StillBridgesTabSwitchesInsideTheSameProfile()
    {
        var cfg = Config();
        var att = new AttributionEngine();
        var t0 = DateTimeOffset.UtcNow;

        Assert.Equal("general", att.Attribute(
            cfg, "chrome.exe", "Calendly", "Chrome.UserData.Profile2", null, "General", t0));

        // same window identity, a moment with no usable signal — the grace period applies
        Assert.Equal("general", att.Attribute(
            cfg, "chrome.exe", "New Tab", "Chrome.UserData.Profile2", null, null, t0.AddSeconds(3)));
    }

    [Fact]
    public void Hold_StillAppliesToNonBrowserApps()
    {
        var cfg = Config();
        cfg.Projects.Add(new ProjectConfig { Name = "editor", Apps = new List<string> { "code.exe" } });
        var att = new AttributionEngine();
        var t0 = DateTimeOffset.UtcNow;

        Assert.Equal("editor", att.Attribute(cfg, "code.exe", "main.cs", "", null, null, t0));
        // non-browser windows carry no per-profile AUMID, so the app-level hold is unchanged
        cfg.Projects.RemoveAll(p => p.Name == "editor");
        Assert.Equal("editor", att.Attribute(cfg, "code.exe", "untitled", "", null, null, t0.AddSeconds(3)));
    }

    [Fact]
    public void ProfileFragment_StillMatchesExtensionLabelOnAnyBrowser()
    {
        var project = new AttributionEngine().Attribute(
            Config(), "chrome.exe", "Calendly", aumid: "", url: null, profileLabel: "General",
            DateTimeOffset.UtcNow);

        Assert.Equal("general", project);
    }
}
