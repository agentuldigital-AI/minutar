using System.IO;
using System.Text.Json;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Pause;

/// <summary>
/// "Oprește tracking-ul" (tray, 2026-08-04): a time-boxed window in which NO activity
/// content is recorded — no window titles, no URLs, no Claude events, no classification,
/// no popups. Unlike a plain gap, the interval itself IS written (paused bucket), so the
/// dashboard can show "pauză 30m" instead of silently missing half an hour.
///
/// Persisted, unlike <see cref="Focus.FocusService"/>: a daemon restart mid-pause (crash,
/// supervisor watchdog) must NOT silently resume recording behind the user's back.
/// </summary>
public sealed class PauseService
{
    private readonly object _lock = new();
    private DateTimeOffset _until;

    public PauseService() => _until = LoadPersisted();

    public bool IsActive => Until is not null;

    public DateTimeOffset? Until
    {
        get
        {
            lock (_lock)
            {
                return _until > DateTimeOffset.UtcNow ? _until : null;
            }
        }
    }

    public void Start(int minutes)
    {
        if (minutes <= 0) return;
        lock (_lock)
        {
            _until = DateTimeOffset.UtcNow.AddMinutes(minutes);
            Persist(_until);
        }
        Log.Info($"Tracking PAUSED for {minutes} min (until {_until.ToLocalTime():HH:mm})");
    }

    public void Resume()
    {
        lock (_lock)
        {
            _until = default;
            Persist(default);
        }
        Log.Info("Tracking resumed");
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "time-tracker", "pause-state.json");

    private static DateTimeOffset LoadPersisted()
    {
        try
        {
            if (!File.Exists(FilePath)) return default;
            var state = JsonSerializer.Deserialize<PersistedPause>(File.ReadAllText(FilePath));
            var until = state?.Until ?? default;
            if (until > DateTimeOffset.UtcNow)
            {
                Log.Info($"Pause restored from disk — tracking stays off until {until.ToLocalTime():HH:mm}");
                return until;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Pause state unreadable, starting unpaused — " + ex.Message);
        }
        return default;
    }

    private static void Persist(DateTimeOffset until)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new PersistedPause(until)));
        }
        catch (Exception ex)
        {
            // best effort: an unwritable state file must not break the pause itself
            Log.Warn("Pause state not persisted — " + ex.Message);
        }
    }

    private sealed record PersistedPause(DateTimeOffset Until);
}
