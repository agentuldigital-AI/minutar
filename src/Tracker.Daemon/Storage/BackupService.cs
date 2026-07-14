using System.Globalization;
using System.IO;
using Microsoft.Extensions.Hosting;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Storage;

/// <summary>
/// Nightly events.db backup via VACUUM INTO (plan graft #6): irreplaceable personal history,
/// and a "just copy the file" instruction would never actually get executed. Runs at ~03:30
/// local, keeps the newest 7 snapshots under %LOCALAPPDATA%\TimeTracker\backups.
/// </summary>
public sealed class BackupService : BackgroundService
{
    private readonly EventStore _store;

    public BackupService(EventStore store) => _store = store;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var next = now.Date.AddDays(now.TimeOfDay >= new TimeSpan(3, 30, 0) ? 1 : 0).AddHours(3.5);
            try
            {
                await Task.Delay(next - now, ct);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            try
            {
                var dir = Path.Combine(Path.GetDirectoryName(_store.DbPath)!, "backups");
                var dest = Path.Combine(dir, $"events-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.db");
                await _store.BackupAsync(dest, ct);
                foreach (var old in Directory.GetFiles(dir, "events-*.db").OrderByDescending(f => f).Skip(7))
                    File.Delete(old);
                Log.Info($"events.db backup written: {dest}");
            }
            catch (Exception ex)
            {
                Log.Warn("nightly backup failed: " + ex.Message);
            }
        }
    }
}
