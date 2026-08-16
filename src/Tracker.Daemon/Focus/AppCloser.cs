using System.Runtime.InteropServices;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Focus;

/// <summary>
/// Focus-mode app close (decision #4 v2): polite WM_CLOSE first (lets apps show save
/// prompts), TerminateProcess only if the SAME pid is still in the foreground 5 s later.
/// The daemon runs elevated (decision #3), so UIPI does not block either step.
/// </summary>
public static class AppCloser
{
    private const uint WM_CLOSE = 0x0010;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint PROCESS_TERMINATE = 0x0001;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static async Task CloseAsync(long hwnd, int pid, string app, Func<int, bool> pidStillForeground)
    {
        if (pid <= 0) return;
        Log.Warn($"Focus: closing '{app}' (pid {pid}) — sending WM_CLOSE");
        SendMessageTimeout(new IntPtr(hwnd), WM_CLOSE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 3000, out _);
        await Task.Delay(TimeSpan.FromSeconds(5));

        if (!pidStillForeground(pid))
        {
            Log.Info($"Focus: '{app}' closed politely");
            return;
        }

        Log.Warn($"Focus: '{app}' still in foreground — TerminateProcess({pid})");
        var handle = OpenProcess(PROCESS_TERMINATE, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            Log.Warn($"Focus: OpenProcess({pid}) failed — cannot terminate");
            return;
        }
        try
        {
            TerminateProcess(handle, 1);
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
