namespace Tracker.Daemon.Calendar;

/// <summary>Ce știm despre un eveniment urmărit. Fără titlu — vezi nota din magazie.</summary>
public sealed class RescheduleEntry
{
    /// <summary>Ultima poziție văzută, ca dată (yyyy-MM-dd).</summary>
    public string Start { get; set; } = "";

    /// <summary>De câte ori l-am văzut mutat mai încolo.</summary>
    public int Moves { get; set; }

    /// <summary>Câte mutări erau la ultima raportare — ca să nu repet același mesaj.</summary>
    public int Reported { get; set; }

    /// <summary>Ultima observație (yyyy-MM-dd), pentru curățenie.</summary>
    public string Seen { get; set; } = "";
}

/// <summary>Un eveniment pe care îl tot împingi mai încolo.</summary>
public sealed record RescheduleAlert(string Title, int Moves, DateOnly Start);

/// <summary>
/// „Ai mutat asta de trei ori." Singurul lucru din tot pachetul care se uită la ce FACI cu
/// planul, nu la ce ai făcut cu timpul.
///
/// Cum e posibil: când muți un eveniment, Google îi păstrează același id și-i schimbă doar
/// data. Ținem local „id → data la care l-am văzut ultima oară" și numărăm mutările înainte.
///
/// Trei reguli fără de care ar fi zgomot, nu informație:
///
/// 1. Seriile recurente sunt sărite. O ședință săptămânală se „mută" legitim în fiecare
///    săptămână; raportată ca amânare, ar apărea de cincizeci de ori pe an și ai învăța să
///    ignori mesajul — adică exact opusul scopului.
/// 2. Numai mutările ÎNAINTE, peste graniță de zi. Mutat mai devreme e opusul amânării, iar
///    mutat la altă oră în aceeași zi e o ajustare, nu o fugă.
/// 3. Fiecare prag se raportează o singură dată. Fără asta, un eveniment amânat de trei ori ar
///    fi pomenit în fiecare zi până se întâmplă.
///
/// Ce NU vede: mutările dintre două observații. Numărăm ce s-a schimbat de la ultima citire a
/// calendarului, deci dacă lași calculatorul închis trei zile, trei mutări arată ca una. Pentru
/// întrebarea „tot amân asta?" e destul.
/// </summary>
public static class RescheduleTracker
{
    /// <summary>Cât timp ținem minte un eveniment pe care nu-l mai vedem (șters sau ieșit din fereastră).</summary>
    private const int KeepDays = 60;

    /// <summary>Plafon, ca fișierul să nu crească la nesfârșit.</summary>
    private const int MaxEntries = 500;

    /// <summary>
    /// Pură dinadins: primește starea veche și ce se vede acum, întoarce starea nouă și ce merită
    /// spus. Așa poate fi testată fără fișiere, fără rețea și fără să treacă timpul.
    /// </summary>
    public static (Dictionary<string, RescheduleEntry> State, List<RescheduleAlert> Alerts) Observe(
        IReadOnlyDictionary<string, RescheduleEntry> known,
        IReadOnlyList<CalendarEvent> upcoming,
        DateOnly today,
        int threshold)
    {
        var state = new Dictionary<string, RescheduleEntry>(StringComparer.Ordinal);
        var alerts = new List<RescheduleAlert>();
        var todayKey = today.ToString("yyyy-MM-dd");

        // Copiem, nu împrumutăm: funcția n-are voie să modifice starea primită.
        foreach (var (id, e) in known)
        {
            if (!DateOnly.TryParse(e.Seen, out var seen) || today.DayNumber - seen.DayNumber > KeepDays)
                continue;
            state[id] = new RescheduleEntry
            {
                Start = e.Start, Moves = e.Moves, Reported = e.Reported, Seen = e.Seen,
            };
        }

        foreach (var ev in upcoming)
        {
            if (ev.Id.Length == 0 || ev.Recurring) continue;

            var start = DateOnly.FromDateTime(ev.Start.LocalDateTime);
            var startKey = start.ToString("yyyy-MM-dd");

            if (!state.TryGetValue(ev.Id, out var entry))
            {
                // prima întâlnire: îl reținem unde e, dar n-avem de la ce să numărăm o mutare
                state[ev.Id] = new RescheduleEntry { Start = startKey, Seen = todayKey };
                continue;
            }

            if (DateOnly.TryParse(entry.Start, out var prev) && start > prev) entry.Moves++;

            entry.Start = startKey;
            entry.Seen = todayKey;

            if (entry.Moves >= threshold && entry.Moves > entry.Reported)
            {
                alerts.Add(new RescheduleAlert(ev.Title, entry.Moves, start));
                entry.Reported = entry.Moves;
            }
        }

        if (state.Count > MaxEntries)
        {
            state = state.OrderByDescending(kv => kv.Value.Seen, StringComparer.Ordinal)
                         .Take(MaxEntries)
                         .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        return (state, alerts.OrderByDescending(a => a.Moves).ThenBy(a => a.Start).ToList());
    }
}
