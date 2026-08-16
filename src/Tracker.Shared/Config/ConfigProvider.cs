using Tracker.Shared.Logging;

namespace Tracker.Shared.Config;

/// <summary>
/// Hot-reloading config source (architecture §3): watches tracker.toml, debounces writes,
/// re-parses atomically, and KEEPS THE LAST GOOD CONFIG on parse errors.
/// </summary>
public sealed class ConfigProvider : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly Timer _debounce;
    private volatile TrackerConfig _current;

    public event Action? Reloaded;

    public ConfigProvider(string configPath)
    {
        ConfigPath = configPath;
        _current = TrackerConfig.Load(configPath);

        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _fsw = new FileSystemWatcher(Path.GetDirectoryName(configPath)!, "*.toml")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _fsw.Changed += (_, _) => _debounce.Change(500, Timeout.Infinite);
        _fsw.Created += (_, _) => _debounce.Change(500, Timeout.Infinite);
        _fsw.Renamed += (_, _) => _debounce.Change(500, Timeout.Infinite);
        _fsw.EnableRaisingEvents = true;
    }

    public string ConfigPath { get; }
    public TrackerConfig Current => _current;

    /// <summary>
    /// Reîncarcă ACUM, sincron. Se cheamă după fiecare scriere venită din dashboard: altfel
    /// răspunsul la POST pleacă înaintea watcher-ului (500 ms debounce), iar următorul GET
    /// citește configul vechi. Simptomul: salvezi clasificările și
    /// nu se întâmplă nimic; abia al doilea click „prinde".
    /// </summary>
    public void ReloadNow() => Reload();

    private void Reload()
    {
        try
        {
            _current = TrackerConfig.Load(ConfigPath);
            Log.Info("Config hot-reloaded");
            Reloaded?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Config reload failed — keeping last good config: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _fsw.Dispose();
        _debounce.Dispose();
    }
}
