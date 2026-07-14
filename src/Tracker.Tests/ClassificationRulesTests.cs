using System.IO;
using Tracker.Daemon.Engine;
using Tracker.Shared.Config;
using Xunit;

namespace Tracker.Tests;

/// <summary>
/// The popup's "mark as productive" now writes ordinary [classification] rules into
/// tracker.toml (2026-07-10). These pin the two things that make that correct:
/// front-inserted rules beat earlier same-type rules, and ConfigWriter round-trips
/// rule ORDER (order is load-bearing — MatchRules takes the first hit per type).
/// </summary>
public sealed class ClassificationRulesTests
{
    private static TrackerConfig BaseConfig()
    {
        var cfg = new TrackerConfig();
        cfg.Classification.Default = "neutral";
        cfg.Classification.Rules.Add(new ClassificationRule { Class = "unproductive", Match = "title", Value = "facebook" });
        cfg.Classification.Rules.Add(new ClassificationRule { Class = "unproductive", Match = "domain", Value = "9gag.com" });
        return cfg;
    }

    [Fact]
    public void FrontInsertedProductiveTitleRule_BeatsEarlierUnproductiveTitleRule()
    {
        var cfg = BaseConfig();
        var engine = new ClassificationEngine();
        var title = "facebook — grup parenting";

        Assert.Equal("unproductive", engine.Classify(cfg, "chrome.exe", title, null, null).Class);

        // what PopupController.UpsertProductiveRule does: remove same target, insert at FRONT
        cfg.Classification.Rules.Insert(0, new ClassificationRule { Class = "productive", Match = "title", Value = title });
        Assert.Equal("productive", engine.Classify(cfg, "chrome.exe", title, null, null).Class);
        // other unproductive pages stay untouched
        Assert.Equal("unproductive", engine.Classify(cfg, "chrome.exe", "facebook — feed", null, null).Class);
    }

    [Fact]
    public void ProductiveDomainRule_ReplacingTheMatchedRule_FlipsTheWholeDomain()
    {
        var cfg = BaseConfig();
        var engine = new ClassificationEngine();
        cfg.Browser.Processes.Add("chrome.exe");

        Assert.Equal("unproductive", engine.Classify(cfg, "chrome.exe", "9GAG", "https://9gag.com/hot", null).Class);

        cfg.Classification.Rules.RemoveAll(r => r.Match == "domain" && r.Value == "9gag.com");
        cfg.Classification.Rules.Insert(0, new ClassificationRule { Class = "productive", Match = "domain", Value = "9gag.com" });
        Assert.Equal("productive", engine.Classify(cfg, "chrome.exe", "9GAG", "https://9gag.com/hot", null).Class);
    }

    [Fact]
    public void ConfigWriter_RoundTrips_RuleOrder_AndYoutubeKeywords()
    {
        var cfg = BaseConfig();
        cfg.Classification.Rules.Insert(0, new ClassificationRule { Class = "productive", Match = "title", Value = "pagina mea" });
        cfg.YoutubeExceptions.TitleKeywords.Add("documentar");

        var path = Path.Combine(Path.GetTempPath(), "tracker-tests", Path.GetRandomFileName() + ".toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            ConfigWriter.Write(cfg, path);
            var loaded = TrackerConfig.Load(path);
            Assert.Equal(
                cfg.Classification.Rules.Select(r => (r.Class, r.Match, r.Value)),
                loaded.Classification.Rules.Select(r => (r.Class, r.Match, r.Value)));
            Assert.Contains("documentar", loaded.YoutubeExceptions.TitleKeywords);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
