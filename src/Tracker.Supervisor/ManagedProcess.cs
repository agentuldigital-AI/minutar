using System.Diagnostics;
using System.IO;
using Tracker.Shared.Logging;

namespace Tracker.Supervisor;

public sealed record ComponentSpec(
    string Name,
    string ExePath,
    string Args,
    Func<Task<bool>> Healthy);

/// <summary>
/// One supervised child (architecture §1.6): starts hidden, restarts on exit or on
/// failed health probes, with exponential backoff (mitigates the silent-death pattern).
/// </summary>
public sealed class ManagedProcess
{
    private readonly ComponentSpec _spec;
    private Process? _proc;
    private int _failures;
    private int _unhealthyStreak;
    private DateTimeOffset _nextStartAllowed = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStart;

    public ManagedProcess(ComponentSpec spec) => _spec = spec;

    public string Name => _spec.Name;
    public string Status { get; private set; } = "pornire...";

    public void EnsureStarted()
    {
        if (_proc is { HasExited: false }) return;
        var now = DateTimeOffset.UtcNow;
        if (now < _nextStartAllowed)
        {
            Status = $"oprit (retry {(int)(_nextStartAllowed - now).TotalSeconds}s)";
            return;
        }
        if (!File.Exists(_spec.ExePath))
        {
            Status = "exe lipsă";
            Log.Warn($"{_spec.Name}: exe not found at {_spec.ExePath}");
            _nextStartAllowed = now.AddSeconds(60);
            return;
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _spec.ExePath,
                Arguments = _spec.Args,
                WorkingDirectory = Path.GetDirectoryName(_spec.ExePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // user-scope .NET installs aren't registered machine-wide, so the child
            // apphosts need DOTNET_ROOT (else exit 0x80008096 "framework missing") —
            // point them at the runtime THIS process is running on
            if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is null or "")
            {
                var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
                var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
                if (File.Exists(Path.Combine(dotnetRoot, "dotnet.exe")))
                    psi.Environment["DOTNET_ROOT"] = dotnetRoot;
            }
            _proc = Process.Start(psi);
            if (_proc is not null) KillOnCloseJob.Assign(_proc); // moare cu supervisorul, chiar și la crash
            _lastStart = now;
            _unhealthyStreak = 0;
            Status = "pornit";
            Log.Info($"{_spec.Name}: started (pid {_proc?.Id})");
        }
        catch (Exception ex)
        {
            Log.Error($"{_spec.Name}: start failed: {ex.Message}");
            ScheduleRetry();
        }
    }

    public async Task CheckAsync()
    {
        var now = DateTimeOffset.UtcNow;

        if (_proc is null || _proc.HasExited)
        {
            if (_proc is { HasExited: true })
            {
                Log.Warn($"{_spec.Name}: exited (code {_proc.ExitCode}) — restarting");
                ScheduleRetry();
                _proc = null;
            }
            EnsureStarted();
            return;
        }

        // grace after start before probing
        if (now - _lastStart < TimeSpan.FromSeconds(20))
        {
            Status = "pornit (grace)";
            return;
        }

        var healthy = false;
        try
        {
            healthy = await _spec.Healthy();
        }
        catch
        {
            // treated as unhealthy
        }

        if (healthy)
        {
            _unhealthyStreak = 0;
            Status = "OK";
            if (now - _lastStart > TimeSpan.FromMinutes(5)) _failures = 0; // stable → reset backoff
            return;
        }

        _unhealthyStreak++;
        Status = $"probe fail x{_unhealthyStreak}";
        if (_unhealthyStreak >= 3)
        {
            Log.Warn($"{_spec.Name}: {_unhealthyStreak} failed probes — killing for restart");
            Stop();
            ScheduleRetry();
        }
    }

    private void ScheduleRetry()
    {
        _failures = Math.Min(_failures + 1, 4);
        var delay = TimeSpan.FromSeconds(Math.Min(120, 15 * Math.Pow(2, _failures - 1)));
        _nextStartAllowed = DateTimeOffset.UtcNow + delay;
        Status = $"restart în {(int)delay.TotalSeconds}s";
    }

    public void Stop()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{_spec.Name}: kill failed: {ex.Message}");
        }
        _proc = null;
    }
}
