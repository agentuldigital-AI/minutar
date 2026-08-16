using System.Globalization;
using System.IO;
using Microsoft.Extensions.Hosting;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Storage;

/// <summary>
/// Copie de siguranță a events.db prin VACUUM INTO (plan graft #6): istoricul personal nu se
/// poate reface, iar o instrucțiune de tip „copiază tu fișierul" n-ar fi executată niciodată.
/// Păstrează cele mai recente 7 exemplare în %LOCALAPPDATA%\TimeTracker\backups.
///
/// Rulează LA PORNIRE, o dată pe zi calendaristică — nu la o oră fixă. Varianta pe oră fixă
/// (03:30) nu s-a declanșat niciodată pe un laptop care noaptea e închis: în trei săptămâni
/// n-a produs niciun exemplar. Mai rău, pornind dimineața aștepta până la 03:30 a doua zi, deci
/// trecea o zi întreagă de lucru fără copie, chiar cu calculatorul pornit tot timpul.
///
/// Bucla de după prima copie acoperă sesiunile lungi: dacă ții daemonul pornit zile la rând,
/// verificarea zilnică produce în continuare câte un exemplar pe zi.
/// </summary>
public sealed class BackupService : BackgroundService
{
    /// <summary>Răgaz după pornire: raportul și briefingul au prioritate, copia nu e urgentă.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>Cât de des se reverifică într-o sesiune lungă.</summary>
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private const int KeepSnapshots = 7;

    private readonly EventStore _store;

    public BackupService(EventStore store) => _store = store;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                BackupIfMissing();
                await Task.Delay(CheckEvery, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // oprire normală
        }
    }

    /// <summary>
    /// Numele fișierului poartă data, deci existența lui E marcajul „azi s-a făcut". Fără stare
    /// separată de ținut sincronizată, și idempotent: cinci reporniri într-o zi dau o copie.
    /// </summary>
    private void BackupIfMissing()
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(_store.DbPath)!, "backups");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, $"events-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.db");
            if (File.Exists(dest)) return;

            _store.BackupAsync(dest, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var old in Directory.GetFiles(dir, "events-*.db").OrderByDescending(f => f).Skip(KeepSnapshots))
                File.Delete(old);
            Log.Info($"events.db backup written: {dest}");
        }
        catch (Exception ex)
        {
            Log.Warn("backup failed: " + ex.Message);
        }
    }
}
