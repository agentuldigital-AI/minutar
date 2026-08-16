using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Forms;
using Tracker.Shared.Config;
using Tracker.Shared.Logging;

namespace Tracker.Launcher;

/// <summary>
/// The clickable entry point (Start Menu / Desktop shortcut). The stack itself must run
/// ELEVATED (decision #3 — a non-elevated watcher silently loses the titles of windows
/// owned by elevated processes), and double-clicking Tracker.Supervisor.exe from Explorer
/// would either start it de-elevated or prompt for UAC every single time. So this launcher
/// never starts the supervisor directly: it triggers the scheduled task that is already
/// registered with "highest privileges", which elevates without a UAC prompt.
///
/// Already running → just opens the dashboard, so the shortcut is safe to click twice.
/// </summary>
internal static class Program
{
    private const string TaskName = "TimeTracker-Supervisor";
    private const string AppTitle = "Minutar";

    /// <summary>Total wait for the daemon to answer /health after the task is triggered.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(900) };

    [STAThread]
    private static int Main(string[] args)
    {
        Log.Init("launcher");
        var bridge = ResolveBridgeUrl(args);

        if (IsUp(bridge))
        {
            Log.Info("Stack already running — opening the dashboard.");
            OpenDashboard(bridge);
            return 0;
        }

        // Two supported install shapes: the elevated scheduled task (install.ps1) and the
        // per-user installer, which has no task at all and runs the supervisor unelevated.
        // Missing task => that second shape, so starting the exe directly IS the right move;
        // an existing task that fails to start is a real error worth surfacing.
        if (ScheduledTaskExists())
        {
            if (!TryStartScheduledTask(out var schtasksError))
            {
                Log.Warn($"Scheduled task '{TaskName}' could not be started: {schtasksError}");
                MessageBox.Show(
                    $"Nu am putut porni sarcina programată „{TaskName}”.\n\n{schtasksError}",
                    AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
        else if (!TryStartSupervisorDirectly(out var startError))
        {
            Log.Warn("Direct supervisor start failed: " + startError);
            MessageBox.Show(
                $"Nu am găsit componentele de pornit.\n\n{startError}",
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        if (!WaitUntilUp(bridge))
        {
            Log.Warn("Task started but the daemon never answered /health.");
            MessageBox.Show(
                "Am pornit sarcina, dar componentele nu au răspuns la timp.\n\n" +
                "Verifică log-urile din %LOCALAPPDATA%\\time-tracker\\logs.",
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        Log.Info("Stack started from the launcher.");
        OpenDashboard(bridge);
        return 0;
    }

    /// <summary>Config is optional here: a missing tracker.toml must not stop the launch.</summary>
    private static string ResolveBridgeUrl(string[] args)
    {
        try
        {
            var cfg = TrackerConfig.Load(ConfigLocator.Resolve(args));
            return $"http://127.0.0.1:{cfg.Server.BridgePort}";
        }
        catch (Exception ex)
        {
            Log.Warn("Config unreadable, falling back to port 5601 — " + ex.Message);
            return "http://127.0.0.1:5601";
        }
    }

    private static bool IsUp(string bridge)
    {
        try
        {
            return Http.GetAsync($"{bridge}/health").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool WaitUntilUp(string bridge)
    {
        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsUp(bridge)) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static bool ScheduledTaskExists()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Per-user install (no scheduled task): the supervisor sits next to us in bin\.</summary>
    private static bool TryStartSupervisorDirectly(out string error)
    {
        error = "";
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Tracker.Supervisor", "Tracker.Supervisor.exe")),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "time-tracker", "bin", "Tracker.Supervisor", "Tracker.Supervisor.exe"),
        };
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            error = "Tracker.Supervisor.exe nu a fost găsit în:\n" + string.Join("\n", candidates);
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryStartScheduledTask(out string error)
    {
        error = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/run /tn \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true, // WinExe + no console: never flash a black window
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
            {
                error = "schtasks.exe nu a putut fi pornit.";
                return false;
            }
            var stderr = p.StandardError.ReadToEnd().Trim();
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            if (p.HasExited && p.ExitCode == 0) return true;
            error = stderr.Length > 0 ? stderr : stdout.Length > 0 ? stdout : $"cod de ieșire {p.ExitCode}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void OpenDashboard(string bridge) =>
        Process.Start(new ProcessStartInfo(bridge) { UseShellExecute = true });
}
