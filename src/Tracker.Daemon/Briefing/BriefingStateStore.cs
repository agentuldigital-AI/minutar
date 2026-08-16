using System.IO;
using System.Text.Json;

namespace Tracker.Daemon.Briefing;

/// <summary>
/// Ce briefing a plecat deja, ca o repornire să nu retrimită.
///
/// Briefingul ZILNIC nu trece pe aici: el are deja un marcaj în DayState, care e exact
/// magazia „o intrare per zi calendaristică" și e protejat de resetarea din interfață.
/// Săptămâna și luna nu încap acolo — DayState e indexat după dată, iar „2026-W33" nu e o
/// dată. De aceea un fișier propriu, după tiparul lui PauseService.
///
/// Cheile sunt șiruri libere („week:2026-W33", „month:2026-08", „phone:2026-08-10..2026-08-17"),
/// ca să nu trebuiască o schemă nouă pentru fiecare tip de briefing adăugat.
/// </summary>
public sealed class BriefingStateStore
{
    /// <summary>Peste atâtea chei, cele mai vechi se taie: fișierul nu are voie să crească la nesfârșit.</summary>
    private const int MaxKeys = 400;

    private readonly object _lock = new();

    private static string Path_ => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "time-tracker", "briefing-state.json");

    private sealed class State
    {
        /// <summary>cheie → când a fost trimis (ISO). Valoarea e pentru diagnostic, nu pentru logică.</summary>
        public Dictionary<string, string> Sent { get; set; } = new();
    }

    private State Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(Path_)) ?? new State();
        }
        catch
        {
            // fișier corupt — o pornim de la zero; în cel mai rău caz retrimitem un briefing
        }
        return new State();
    }

    public bool WasSent(string key)
    {
        lock (_lock) return Load().Sent.ContainsKey(key);
    }

    public void MarkSent(string key)
    {
        lock (_lock)
        {
            var s = Load();
            s.Sent[key] = DateTimeOffset.Now.ToString("o");
            if (s.Sent.Count > MaxKeys)
            {
                s.Sent = s.Sent.OrderByDescending(kv => kv.Value, StringComparer.Ordinal)
                               .Take(MaxKeys)
                               .ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
                File.WriteAllText(Path_, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // best-effort, ca la DayStateStore: un marcaj nescris înseamnă cel mult un mesaj în plus
            }
        }
    }
}
