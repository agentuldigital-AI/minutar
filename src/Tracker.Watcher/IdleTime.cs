using System.Runtime.InteropServices;

namespace Tracker.Watcher;

internal static class IdleTime
{
    /// <summary>Time since the last user input in this session (GetLastInputInfo).</summary>
    public static TimeSpan Get()
    {
        var lii = new Win32.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Win32.LASTINPUTINFO>() };
        if (!Win32.GetLastInputInfo(ref lii)) return TimeSpan.Zero;

        // dwTime is a 32-bit tick count: subtract in uint space so the 49.7-day wrap cancels
        // out. Only near-wrap values (dwTime slightly "in the future") are the documented
        // non-monotonic jitter and clamp to zero — a plain int cast would also swallow every
        // real idle between ~24.9 and ~49.7 days (tournament review finding).
        var now32 = unchecked((uint)Win32.GetTickCount64());
        var deltaMs = unchecked(now32 - lii.dwTime);
        return deltaMs > uint.MaxValue - 60_000u ? TimeSpan.Zero : TimeSpan.FromMilliseconds(deltaMs);
    }
}
