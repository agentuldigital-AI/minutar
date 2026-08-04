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
    public void AumidLearnedForOneProfile_IsProofAgainstAnother()
    {
        var store = new BrowserStateStore();
        store.Update(Hb("Client B", "Client B", "https://clientb.example/", focused: true));
        store.Update(Hb("General", "Calendly", "https://calendly.com/app", focused: false));
        store.LearnAumid("chrome", "Client B", "Chrome.UserData.Profile1");
        store.LearnAumid("chrome", "General", "Chrome.UserData.Profile2");

        // a window of profile 2 must not be credited to the instance of profile 1
        Assert.True(store.AumidBelongsToOtherProfile("chrome", "Client B", "Chrome.UserData.Profile2"));
        Assert.False(store.AumidBelongsToOtherProfile("chrome", "Client B", "Chrome.UserData.Profile1"));
    }

    [Theory]
    // never seen (mapping still being learned) and empty (watcher gave us nothing)
    [InlineData("Chrome.UserData.Profile9")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownAumid_IsNotTreatedAsProof(string? aumid)
    {
        var store = new BrowserStateStore();
        store.LearnAumid("chrome", "Client B", "Chrome.UserData.Profile1");

        Assert.False(store.AumidBelongsToOtherProfile("chrome", "Client B", aumid));
    }

    [Fact]
    public void AumidOfAnotherBrowser_IsNotProof()
    {
        // Edge reports one AUMID for every profile, so it must never veto a Chrome instance
        var store = new BrowserStateStore();
        store.LearnAumid("edge", "General", "MSEdge");

        Assert.False(store.AumidBelongsToOtherProfile("chrome", "Client B", "MSEdge"));
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
