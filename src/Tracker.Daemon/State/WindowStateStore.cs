namespace Tracker.Daemon.State;

/// <summary>Mirror of the watcher's current window+AFK state (architecture §1.3 bridge).
/// Hwnd/Pid enable the focus-mode app close (F4); default to 0 for older watchers.</summary>
public sealed record WindowState(
    string App, string Title, string Aumid, bool Afk, DateTimeOffset Timestamp,
    long Hwnd = 0, int Pid = 0);

public sealed class WindowStateStore
{
    private readonly object _lock = new();
    private WindowState? _current;
    private DateTimeOffset _lastUpdate;

    public void Update(WindowState state)
    {
        lock (_lock)
        {
            _current = state;
            _lastUpdate = DateTimeOffset.UtcNow;
        }
    }

    public (WindowState? State, DateTimeOffset LastUpdate) Snapshot()
    {
        lock (_lock)
        {
            return (_current, _lastUpdate);
        }
    }
}
