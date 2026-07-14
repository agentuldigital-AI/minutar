using Tracker.Shared.Aw;
using Tracker.Shared.Config;
using Tracker.Shared.Logging;
using Tracker.Watcher;

// Tracker.Watcher — window + AFK capture (M1, architecture §1.2).
// Flags: --print-config (validate TOML), --smoke (aw-server round-trip), --config <path>.

Log.Init("watcher");

try
{
    var cfgPath = ConfigLocator.Resolve(args);
    var cfg = TrackerConfig.Load(cfgPath);
    Log.Info($"Config loaded from {cfgPath}");
    Log.Info($"  aw-server: {cfg.Server.AwUrl} | bridge port: {cfg.Server.BridgePort} | bucket host: {cfg.Server.ResolveBucketHost()}");
    Log.Info($"  afk: timeout {cfg.Afk.TimeoutSeconds}s poll {cfg.Afk.PollSeconds}s | window: poll {cfg.Window.PollSeconds}s pulsetime {cfg.Window.PulsetimeSeconds}s");

    if (args.Contains("--print-config"))
    {
        foreach (var p in cfg.Projects)
            Log.Info($"  project '{p.Name}': keywords=[{string.Join(", ", p.Keywords)}] claude_dirs=[{string.Join(", ", p.ClaudeDirs)}] browser_profiles=[{string.Join(", ", p.BrowserProfiles)}]");
        Log.Info($"  classification: default={cfg.Classification.Default}, {cfg.Classification.Rules.Count} rule(s)");
        foreach (var r in cfg.Classification.Rules)
            Log.Info($"    [{r.Class}] {r.Match} = {r.Value}");
        Log.Info($"  popup: grace {cfg.Popup.GraceSeconds}s, postpone [{string.Join(",", cfg.Popup.PostponeOptionsMinutes)}] min, re-nag {cfg.Popup.RenagMinutesDefault} min");
        Log.Info($"  youtube exceptions: {cfg.YoutubeExceptions.TitleKeywords.Count} title keyword(s), {cfg.YoutubeExceptions.Channels.Count} channel(s)");
        Log.Info($"  claude: {cfg.Claude.ProjectsDir} | work pulsetime {cfg.Claude.WorkPulsetimeSeconds}s | attention pulsetime {cfg.Claude.AttentionPulsetimeSeconds}s");
        Log.Info("PRINT-CONFIG OK");
        return 0;
    }

    if (args.Contains("--smoke"))
    {
        var host = cfg.Server.ResolveBucketHost();
        using var aw = new AwClient(cfg.Server.AwUrl, "tracker-watcher", host);

        Log.Info("Smoke 1/4: GET /api/0/info ...");
        Log.Info("  " + await aw.GetInfoAsync());

        var bucket = $"tracker-smoke_{host}";
        Log.Info($"Smoke 2/4: ensure bucket '{bucket}' (type tracker.smoke) ...");
        await aw.EnsureBucketAsync(bucket, "tracker.smoke");

        Log.Info("Smoke 3/4: two heartbeats, pulsetime 60s (should merge into one event) ...");
        var data = new Dictionary<string, object?> { ["status"] = "smoke-ok", ["milestone"] = "M0" };
        await aw.HeartbeatAsync(bucket, data, 60);
        await Task.Delay(1100);
        await aw.HeartbeatAsync(bucket, data, 60);

        Log.Info("Smoke 4/4: events readback ...");
        Log.Info("  " + await aw.GetEventsAsync(bucket, 5));
        Log.Info("SMOKE PASS (bucket 'tracker-smoke_*' can be deleted from aw-webui when no longer needed)");
        return 0;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    using var provider = new ConfigProvider(cfgPath);
    using var service = new WatcherService(provider);
    await service.RunAsync(cts.Token);
    return 0;
}
catch (Exception ex)
{
    Log.Error("Fatal: " + ex);
    return 1;
}
