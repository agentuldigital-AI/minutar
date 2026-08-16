namespace Tracker.Watcher;

/// <summary>One capture of the foreground window: exe name, title, AUMID + hwnd/pid (focus-mode close, F4).</summary>
public sealed record WindowSnapshot(string App, string Title, string Aumid, long Hwnd, int Pid)
{
    public static WindowSnapshot? Capture()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null; // lock screen / desktop transition

        // the window can be destroyed between any two calls below — every step tolerates it
        var title = GetTitle(hwnd);
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        var app = GetProcessName(pid);
        var aumid = AumidReader.TryGet(hwnd);
        return new WindowSnapshot(app, title, aumid, hwnd.ToInt64(), (int)pid);
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var len = Win32.GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var buf = new char[len + 1];
        var copied = Win32.GetWindowText(hwnd, buf, buf.Length);
        return copied > 0 ? new string(buf, 0, copied) : "";
    }

    private static string GetProcessName(uint pid)
    {
        if (pid == 0) return "unknown";

        var handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return "unknown";
        try
        {
            var buf = new char[1024];
            var size = (uint)buf.Length;
            if (!Win32.QueryFullProcessImageName(handle, 0, buf, ref size) || size == 0)
                return "unknown";
            return Path.GetFileName(new string(buf, 0, (int)size));
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }
}
