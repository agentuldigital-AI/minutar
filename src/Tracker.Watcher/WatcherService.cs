using System.Net.Http;
using System.Net.Http.Json;
using Tracker.Shared.Aw;
using Tracker.Shared.Config;
using Tracker.Shared.Logging;

namespace Tracker.Watcher;

/// <summary>
/// The M1 core (architecture §1.2): hybrid window capture (SetWinEventHook for instant
/// switches + poll as safety net) and GetLastInputInfo AFK detection. Heartbeats go
/// DIRECTLY to aw-server; the current state is mirrored to the daemon fire-and-forget.
/// Hardened per the M1 tournament review: capture starts immediately (queueing until
/// buckets are ensured), hook-triggered captures are throttled, loops fail fast on
/// unexpected exceptions so the watchdog restarts a healthy process.
/// </summary>
public sealed class WatcherService : IDisposable
{
    private static readonly TimeSpan MinCaptureInterval = TimeSpan.FromMilliseconds(300);

    private readonly ConfigProvider _config;
    private readonly AwClient _aw;
    private readonly ResilientAwClient _resilient;
    private readonly HttpClient _mirror;
    private readonly string _windowBucket;
    private readonly string _afkBucket;
    private readonly SemaphoreSlim _pollSignal = new(0, 1);

    private Thread? _hookThread;
    private volatile bool _hookShutdown;
    private uint _hookThreadId;
    private Win32.WinEventDelegate? _hookDelegate; // field reference keeps the delegate alive for the native hook
    private IntPtr _hookForeground;
    private IntPtr _hookNameChange;

    private volatile bool _afk;

    public WatcherService(ConfigProvider config)
    {
        _config = config;
        var cfg = config.Current;
        var host = cfg.Server.ResolveBucketHost();
        _aw = new AwClient(cfg.Server.AwUrl, "tracker-watcher", host);
        // gated until the buckets exist; heartbeats queue with original timestamps meanwhile
        _resilient = new ResilientAwClient(_aw, startReady: false);
        _resilient.OnClientError = () => EnsureBucketsOnceAsync(CancellationToken.None);
        _mirror = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{cfg.Server.BridgePort}"),
            Timeout = TimeSpan.FromSeconds(2),
        };
        _windowBucket = AwBuckets.Window(host);
        _afkBucket = AwBuckets.Afk(host);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        StartHookThread();
        var cfg = _config.Current;
        Log.Info($"Watcher running: window poll {cfg.Window.PollSeconds}s + event hooks, AFK timeout {cfg.Afk.TimeoutSeconds}s poll {cfg.Afk.PollSeconds}s (hot-reload on)");
        try
        {
            // capture starts IMMEDIATELY — bucket creation retries in parallel and opens
            // the resilient client's gate on success (review blind-spot fix)
            await Task.WhenAll(EnsureBucketsLoopAsync(ct), WindowLoopAsync(ct), AfkLoopAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        StopHookThread();
        Log.Info("Watcher stopped.");
    }

    private async Task EnsureBucketsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _aw.EnsureBucketAsync(_windowBucket, AwBuckets.WindowType, ct);
                await _aw.EnsureBucketAsync(_afkBucket, AwBuckets.AfkType, ct);
                _resilient.MarkReady();
                Log.Info("Buckets ensured — queued heartbeats draining");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // shutdown
            }
            catch (Exception ex)
            {
                // catch EVERYTHING: an unexpected exception here used to end this loop
                // silently — the ready-gate then never opened and the queue dropped every
                // heartbeat while the process looked alive (zombie watcher)
                Log.Warn($"bucket creation failed ({ex.GetType().Name}: {ex.Message}), retrying in 10s ...");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    private async Task EnsureBucketsOnceAsync(CancellationToken ct)
    {
        try
        {
            await _aw.EnsureBucketAsync(_windowBucket, AwBuckets.WindowType, ct);
            await _aw.EnsureBucketAsync(_afkBucket, AwBuckets.AfkType, ct);
        }
        catch (Exception ex)
        {
            Log.Warn("re-ensure buckets failed: " + ex.Message);
        }
    }

    private async Task WindowLoopAsync(CancellationToken ct)
    {
        var lastCapture = DateTimeOffset.MinValue;
        // diagnostic 2026-07-11 (goluri de 3-60s în plină activitate — sursă necunoscută):
        // separă cei trei suspecți: secure desktop (capturi nule), heartbeat-uri lente
        // (tee/aw), stall-uri de proces. De citit logul după o zi, apoi fixul țintit.
        DateTimeOffset? nullSince = null;
        var lastIterEnd = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // hook events can fire many times per second (title marquees, build
                // progress) — enforce a floor so we can't flood aw-server (review finding)
                var sinceLast = DateTimeOffset.UtcNow - lastCapture;
                // a backward clock step makes sinceLast negative — treat it as "long ago",
                // otherwise the loop would sleep for the whole step size (frozen bucket)
                if (sinceLast >= TimeSpan.Zero && sinceLast < MinCaptureInterval)
                    await Task.Delay(MinCaptureInterval - sinceLast, ct);

                var iterStart = DateTimeOffset.UtcNow;
                if (lastIterEnd != DateTimeOffset.MinValue && iterStart - lastIterEnd > TimeSpan.FromSeconds(4))
                    Log.Info($"[diag] window loop stalled {(iterStart - lastIterEnd).TotalSeconds:F1}s (sistem încărcat / proces throttled?)");

                var cfg = _config.Current;
                var snap = WindowSnapshot.Capture();
                lastCapture = DateTimeOffset.UtcNow;
                if (snap is not null)
                {
                    if (nullSince is { } ns && lastCapture - ns > TimeSpan.FromSeconds(2))
                        Log.Info($"[diag] fereastră necapturabilă timp de {(lastCapture - ns).TotalSeconds:F0}s (secure desktop: UAC / ecran PIN / lock?)");
                    nullSince = null;

                    var data = new Dictionary<string, object?>
                    {
                        ["app"] = snap.App,
                        ["title"] = snap.Title,
                        ["aumid"] = snap.Aumid,
                    };
                    var hbStart = DateTimeOffset.UtcNow;
                    await _resilient.HeartbeatAsync(_windowBucket, data, cfg.Window.PulsetimeSeconds, ct: ct);
                    var hbMs = (DateTimeOffset.UtcNow - hbStart).TotalMilliseconds;
                    if (hbMs > 1000)
                        Log.Info($"[diag] heartbeat de fereastră lent: {hbMs:F0}ms (queued={_resilient.QueuedCount})");
                    MirrorFireAndForget(snap);
                }
                else
                {
                    nullSince ??= lastCapture;
                }

                // poll interval, cut short instantly by the event hook on a window/title switch
                await _pollSignal.WaitAsync(TimeSpan.FromSeconds(cfg.Window.PollSeconds), ct);
                lastIterEnd = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // fail FAST and loud: a half-dead watcher silently corrupts the data;
                // Task Scheduler / the supervisor restarts a fresh process
                Log.Error("WindowLoop fatal — exiting for restart: " + ex);
                Environment.Exit(1);
            }
        }
    }

    private async Task AfkLoopAsync(CancellationToken ct)
    {
        var wasAfk = false;
        var lastTick = default(DateTimeOffset);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = _config.Current;
                int timeout = cfg.Afk.TimeoutSeconds;
                int poll = cfg.Afk.PollSeconds;
                double afkPulse = timeout + 3 * poll; // must bridge the backfill gap on the first afk tick
                double activePulse = 2 * poll + 2;

                var idle = IdleTime.Get();
                var isAfk = idle.TotalSeconds >= timeout;
                var now = DateTimeOffset.UtcNow;
                _afk = isAfk;

                // PC dormant (sleep / Modern Standby freezes the whole process — verified
                // 2026-07-11: the 11:24-11:51 locked span left a hole in BOTH stores while
                // this loop was alive): a clock jump between ticks means nothing could be
                // recorded, so backfill the frozen span as AFK. The onset-backfill below
                // can't cover it — wasAfk is already true across the freeze, no transition.
                if (lastTick != default && now - lastTick > TimeSpan.FromSeconds(Math.Max(60, 6 * poll)))
                {
                    var frozen = now - lastTick;
                    // sleep vs FORWARD clock step: after a real sleep the input idle covers
                    // the frozen span (GetTickCount64 keeps counting through suspend); a
                    // clock step while the user is typing leaves idle SMALL — backfilling
                    // that as AFK would erase real active work
                    if (idle >= frozen - TimeSpan.FromSeconds(2 * poll))
                    {
                        Log.Info($"Resume detected: clock jumped {frozen.TotalMinutes:F1} min — backfilling the dormant span as AFK");
                        await _resilient.HeartbeatAsync(_afkBucket, AfkData(true), afkPulse, lastTick, frozen.TotalSeconds, ct);
                        wasAfk = true; // the span ended as a pause; the logic below re-evaluates the present
                    }
                    else
                    {
                        Log.Info($"Clock jumped forward {frozen.TotalMinutes:F1} min while input was recent (idle {idle.TotalSeconds:F0}s) — NOT backfilled as AFK");
                    }
                }
                lastTick = now;

                if (isAfk && !wasAfk)
                {
                    // AW semantics: the AFK period starts at the LAST INPUT, not at detection
                    // time. Explicit duration covers arbitrary gaps (sleep/hibernate resume,
                    // watcher restart) that pulsetime alone cannot bridge (review finding).
                    var since = now - idle;
                    Log.Info($"AFK since {since.ToLocalTime():HH:mm:ss} (idle {idle.TotalSeconds:F0}s) — backfilling");
                    await _resilient.HeartbeatAsync(_afkBucket, AfkData(true), afkPulse, since, idle.TotalSeconds, ct);
                }
                else if (!isAfk && wasAfk)
                {
                    Log.Info("Back from AFK");
                }

                await _resilient.HeartbeatAsync(_afkBucket, AfkData(isAfk), isAfk ? afkPulse : activePulse, now, 0, ct);
                wasAfk = isAfk;

                await Task.Delay(TimeSpan.FromSeconds(poll), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("AfkLoop fatal — exiting for restart: " + ex);
                Environment.Exit(1);
            }
        }
    }

    private static Dictionary<string, object?> AfkData(bool afk) =>
        new() { ["status"] = afk ? "afk" : "not-afk" };

    private void MirrorFireAndForget(WindowSnapshot snap)
    {
        var payload = new
        {
            app = snap.App,
            title = snap.Title,
            aumid = snap.Aumid,
            afk = _afk,
            hwnd = snap.Hwnd,
            pid = snap.Pid,
            timestamp = DateTimeOffset.UtcNow,
        };
        _ = _mirror.PostAsJsonAsync("/window/state", payload)
            .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    // --- event-hook thread (hooks need a message loop or they silently unregister) ---

    private void StartHookThread()
    {
        _hookThread = new Thread(HookThreadBody) { IsBackground = true, Name = "win-event-hook" };
        _hookThread.Start();
    }

    private void HookThreadBody()
    {
        _hookThreadId = Win32.GetCurrentThreadId();
        if (_hookShutdown) return; // shutdown raced thread start (review PLAUSIBLE finding)

        _hookDelegate = OnWinEvent;
        const uint flags = Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS;
        _hookForeground = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookDelegate, 0, 0, flags);
        _hookNameChange = Win32.SetWinEventHook(
            Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _hookDelegate, 0, 0, flags);
        if (_hookForeground == IntPtr.Zero || _hookNameChange == IntPtr.Zero)
            Log.Warn("SetWinEventHook failed for one or both events — running poll-only");

        while (!_hookShutdown && Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        if (_hookForeground != IntPtr.Zero) Win32.UnhookWinEvent(_hookForeground);
        if (_hookNameChange != IntPtr.Zero) Win32.UnhookWinEvent(_hookNameChange);
        Log.Info("Hook thread exited");
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Win32.OBJID_WINDOW) return;
        // NAMECHANGE fires for every window in the system — only the foreground one matters
        if (eventType == Win32.EVENT_OBJECT_NAMECHANGE && hwnd != Win32.GetForegroundWindow()) return;
        try
        {
            _pollSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // a wake is already pending
        }
        catch (ObjectDisposedException)
        {
            // shutdown race — semaphore already disposed
        }
    }

    private void StopHookThread()
    {
        _hookShutdown = true;
        if (_hookThread is null) return;
        for (var i = 0; i < 5 && _hookThreadId != 0; i++)
        {
            if (Win32.PostThreadMessage(_hookThreadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero)) break;
            Thread.Sleep(50);
        }
        _hookThread.Join(TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        _resilient.Dispose(); // also disposes _aw
        _mirror.Dispose();
        _pollSignal.Dispose();
    }
}
