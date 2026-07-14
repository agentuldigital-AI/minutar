using Tracker.Shared.Config;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Focus;

/// <summary>Focus mode state (F4, decision #4 v2): time-boxed strict enforcement window.</summary>
public sealed class FocusService
{
    private readonly ConfigProvider _config;
    private long _untilTicks; // UTC ticks; 0 = off

    public FocusService(ConfigProvider config) => _config = config;

    public bool IsActive => DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _untilTicks);

    public DateTimeOffset? Until
    {
        get
        {
            var t = Interlocked.Read(ref _untilTicks);
            return t > DateTimeOffset.UtcNow.UtcTicks ? new DateTimeOffset(t, TimeSpan.Zero) : null;
        }
    }

    public void Start(int? minutes)
    {
        var m = minutes is > 0 ? minutes.Value : _config.Current.Focus.DefaultMinutes;
        Interlocked.Exchange(ref _untilTicks, DateTimeOffset.UtcNow.AddMinutes(m).UtcTicks);
        Log.Info($"Focus mode ON ({m} min)");
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _untilTicks, 0);
        Log.Info("Focus mode OFF");
    }

    /// <summary>Domains the extension should close instantly while focus is on.</summary>
    public List<string> BlockedDomains()
    {
        if (!IsActive || !_config.Current.Focus.CloseTabs) return new List<string>();
        return _config.Current.Classification.Rules
            .Where(r => r.Class == "unproductive" && r.Match == "domain" && r.Value.Length > 0)
            .Select(r => r.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
