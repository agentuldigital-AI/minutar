using Tracker.Daemon.State;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// A browser profile without the extension reports nothing, so the foreground window has no
/// matching tab. It must NOT inherit the identity of the profile that does report — that is
/// how a Calendly tab in a plain Chrome profile ended up as "Client B · Productiv" (2026-08-04).
/// </summary>
public sealed class BrowserProfileLeakTests
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(90);

    private static BrowserHeartbeat Hb(string profile, string title, string url, bool focused) =>
        new(Url: url, Title: title, Audible: false, AnyAudible: false, Incognito: false,
            TabCount: 1, Channel: null, Profile: profile, Email: null, Focused: focused,
            Browser: "chrome");

    [Fact]
    public void NewTabInAnotherProfile_DoesNotMatchOurNewTabWindow()
    {
        // the exact 2026-08-04 leak: a client profile sitting on chrome://newtab, while the
        // user tabs around a profile without the extension whose window is also "New Tab"
        var store = new BrowserStateStore();
        store.Update(Hb("Client A", "New Tab", "chrome://newtab/", focused: true));

        var best = store.BestFor("New Tab - Google Chrome", "chrome.exe", Fresh, "Chrome");

        Assert.Null(best);
    }

    [Theory]
    [InlineData("Settings", "chrome://settings")]
    [InlineData("Downloads", "edge://downloads/all")]
    [InlineData("Untitled", "about:blank")]
    public void InternalPagesNeverProveIdentityByTitle(string tabTitle, string url)
    {
        var store = new BrowserStateStore();
        store.Update(Hb("Client A", tabTitle, url, focused: true));

        Assert.Null(store.BestFor($"{tabTitle} - Google Chrome", "chrome.exe", Fresh, "Chrome"));
    }

    [Fact]
    public void InternalPageIsAccepted_WhenTheAumidConfirmsTheProfile()
    {
        // being ON that profile's own new tab must still attribute normally
        var store = new BrowserStateStore();
        store.Update(Hb("Client A", "New Tab", "chrome://newtab/", focused: true));
        store.LearnAumid("chrome", "Client A", "Chrome.UserData.Profile2");

        var best = store.BestFor("New Tab - Google Chrome", "chrome.exe", Fresh, "Chrome.UserData.Profile2");

        Assert.Equal("Client A", best!.Profile);
    }

    [Fact]
    public void RealPageWithAMatchingTitle_IsStillRejectedWhenTheAumidContradicts()
    {
        // same document open in two profiles: the title matches, the window does not
        var store = new BrowserStateStore();
        store.Update(Hb("Client A", "ClientC_Structura - Google Slides", "https://docs.google.com/x", focused: true));
        store.LearnAumid("chrome", "Client A", "Chrome.UserData.Profile2");

        var best = store.BestFor("ClientC_Structura - Google Slides - Google Chrome", "chrome.exe",
            Fresh, "Chrome.UserData.Profile1");

        Assert.Null(best);
    }

    [Fact]
    public void WritePath_RejectsAnInternalPageClaimingSomeoneElsesWindow()
    {
        // observed live: the bar was already clean, but the heartbeat still landed in the
        // web bucket, so the REPORT kept crediting the other profile
        var store = new BrowserStateStore();
        var hb = Hb("Client A", "New Tab", "chrome://newtab/", focused: true);
        store.Update(hb);

        Assert.False(store.CanClaimForegroundWindow(hb, "Chrome.UserData.Profile2"));
    }

    [Fact]
    public void WritePath_AcceptsAnInternalPageOnItsOwnConfirmedWindow()
    {
        var store = new BrowserStateStore();
        var hb = Hb("Client A", "New Tab", "chrome://newtab/", focused: true);
        store.Update(hb);
        store.LearnAumid("chrome", "Client A", "Chrome.UserData.Profile2");

        Assert.True(store.CanClaimForegroundWindow(hb, "Chrome.UserData.Profile2"));
    }

    [Fact]
    public void WritePath_AcceptsARealPageWhileNothingIsKnownYet()
    {
        // cold start: no pairing learned, ordinary page — behave exactly as before
        var store = new BrowserStateStore();
        var hb = Hb("Client B", "Client B", "https://clientb.example/", focused: true);
        store.Update(hb);

        Assert.True(store.CanClaimForegroundWindow(hb, "Chrome.UserData.Profile1"));
    }

    [Fact]
    public void ForegroundWindowWithNoMatchingTab_GetsNoBrowserState()
    {
        var store = new BrowserStateStore();
        // the only instance with the extension still claims focus (blur report never landed)
        store.Update(Hb("Client B", "Client B — panou", "https://clientb.example/admin", focused: true));

        var best = store.BestFor("Calendly - Google Chrome", "chrome.exe", Fresh);

        Assert.Null(best);
    }

    [Fact]
    public void ForegroundWindowMatchingATab_StillResolves()
    {
        var store = new BrowserStateStore();
        store.Update(Hb("Client B", "Client B — panou", "https://clientb.example/admin", focused: true));

        var best = store.BestFor("Client B — panou - Google Chrome", "chrome.exe", Fresh);

        Assert.NotNull(best);
        Assert.Equal("Client B", best!.Profile);
    }

    [Fact]
    public void CompetingProfiles_TheOneMatchingTheWindowTitleWins()
    {
        var store = new BrowserStateStore();
        store.Update(Hb("Client B", "Client B", "https://clientb.example/", focused: true));
        store.Update(Hb("General", "Calendly", "https://calendly.com/app", focused: false));

        var best = store.BestFor("Calendly - Google Chrome", "chrome.exe", Fresh);

        Assert.Equal("General", best!.Profile);
    }

    [Fact]
    public void AumidLearnedForOneProfile_RejectsAnotherProfilesWindow()
    {
        var store = new BrowserStateStore();
        store.Update(Hb("Client B", "Client B", "https://clientb.example/", focused: true));
        store.Update(Hb("General", "Calendly", "https://calendly.com/app", focused: false));
        store.LearnAumid("chrome", "Client B", "Chrome.UserData.Profile1");
        store.LearnAumid("chrome", "General", "Chrome.UserData.Profile2");

        // a window of profile 2 must not be credited to the instance of profile 1
        Assert.False(store.AumidCompatible("chrome", "Client B", "Chrome.UserData.Profile2"));
        Assert.True(store.AumidCompatible("chrome", "Client B", "Chrome.UserData.Profile1"));
    }

    [Theory]
    // a window we have never seen for a profile we DO know is rejected; no AUMID at all
    // (watcher gave us nothing) can never be used to reject
    [InlineData("Chrome.UserData.Profile9", false)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void UnknownAumid_IsJudgedAgainstWhatWeKnow(string? aumid, bool expectCompatible)
    {
        var store = new BrowserStateStore();
        store.LearnAumid("chrome", "Client B", "Chrome.UserData.Profile1");

        Assert.Equal(expectCompatible, store.AumidCompatible("chrome", "Client B", aumid));
    }

    [Fact]
    public void AumidOfAnotherBrowser_DoesNotRejectUs()
    {
        // Edge reports one AUMID for every profile, so it must never veto a Chrome instance
        var store = new BrowserStateStore();
        store.LearnAumid("edge", "General", "MSEdge");

        Assert.True(store.AumidCompatible("chrome", "Client B", "MSEdge"));
    }

    [Fact]
    public void EmptyWindowTitle_KeepsTheFocusedFallback()
    {
        // no title to reason about (watcher mirror gap) — the old behaviour is still the
        // best guess available, so it stays
        var store = new BrowserStateStore();
        store.Update(Hb("Client B", "Client B", "https://clientb.example/", focused: true));

        var best = store.BestFor("", "chrome.exe", Fresh);

        Assert.Equal("Client B", best!.Profile);
    }
}
