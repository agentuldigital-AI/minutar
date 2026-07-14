using System.IO;
using System.Text.Json;

namespace Tracker.Daemon.Coach;

public sealed class DayPriority
{
    public string Text { get; set; } = "";
    public string Project { get; set; } = "";
    public bool Done { get; set; }
}

public sealed class NudgeRecord
{
    public string Time { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>One day of coach state: morning intent, top-3 priorities, shutdown review.</summary>
public sealed class DayState
{
    public string Date { get; set; } = "";
    public string Intent { get; set; } = "";
    public List<DayPriority> Priorities { get; set; } = new();
    public string ShutdownNotes { get; set; } = "";
    public string TomorrowPlan { get; set; } = "";
    public bool IntentPromptShown { get; set; }
    public bool ShutdownPromptShown { get; set; }
    public List<NudgeRecord> Nudges { get; set; } = new();
}

/// <summary>JSON per day in %LOCALAPPDATA%\time-tracker\coach\ (machine state, not repo config).</summary>
public sealed class DayStateStore
{
    private readonly object _lock = new();
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "time-tracker", "coach");

    private static string PathFor(string date) => Path.Combine(Dir, $"day-{date}.json");

    public DayState Load(string date)
    {
        lock (_lock)
        {
            try
            {
                var p = PathFor(date);
                if (File.Exists(p))
                    return JsonSerializer.Deserialize<DayState>(File.ReadAllText(p)) ?? New(date);
            }
            catch
            {
                // corrupt file — start fresh
            }
            return New(date);
        }
    }

    public void Save(DayState state)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                state.Priorities = state.Priorities.Take(3).ToList(); // hard cap: top 3
                File.WriteAllText(PathFor(state.Date), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // best-effort
            }
        }
    }

    public DayState Today() => Load(DateTimeOffset.Now.ToString("yyyy-MM-dd"));

    public void Mutate(Action<DayState> change)
    {
        lock (_lock)
        {
            var s = Today();
            change(s);
            Save(s);
        }
    }

    private static DayState New(string date) => new() { Date = date };
}
