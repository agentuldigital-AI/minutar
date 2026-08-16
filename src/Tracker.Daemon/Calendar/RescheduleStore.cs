using System.IO;
using System.Text.Json;

namespace Tracker.Daemon.Calendar;

/// <summary>
/// Unde ținem minte pe unde era fiecare eveniment, ca să știm când l-ai mutat. După tiparul lui
/// <see cref="Briefing.BriefingStateStore"/>: fișier JSON în %LOCALAPPDATA%, scriere best-effort,
/// fișier corupt = o luăm de la zero (în cel mai rău caz pierdem numărătoarea, nu date).
///
/// NU ține titluri, dinadins. Ar fi fost mai simplu să le salvez odată cu restul, dar atunci
/// numele clienților tăi ar sta pe disc într-un al doilea loc, fără motiv: titlul e oricum în
/// evenimentul citit acum, exact când am nevoie de el pentru mesaj. Aici stau doar id-ul de la
/// Google și niște date calendaristice.
/// </summary>
public sealed class RescheduleStore
{
    private readonly object _lock = new();

    private static string Path_ => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "time-tracker", "reschedule-state.json");

    private sealed class State
    {
        public Dictionary<string, RescheduleEntry> Events { get; set; } = new();
    }

    public Dictionary<string, RescheduleEntry> Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(Path_))
                    return JsonSerializer.Deserialize<State>(File.ReadAllText(Path_))?.Events ?? new();
            }
            catch
            {
                // fișier corupt — repornim numărătoarea; nimic ireversibil
            }
            return new Dictionary<string, RescheduleEntry>(StringComparer.Ordinal);
        }
    }

    public void Save(Dictionary<string, RescheduleEntry> events)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
                File.WriteAllText(Path_, JsonSerializer.Serialize(
                    new State { Events = events }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // best-effort: o stare nescrisă înseamnă cel mult o mutare nenumărată
            }
        }
    }
}
