using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tracker.Supervisor;

/// <summary>
/// Renders the dynamic tray icon: filled dot = active (green/red/gray by class),
/// hollow dot = AFK, dim hollow = daemon offline. Same hex values as the dashboard
/// palette (validated for color-blindness; the tooltip text always carries the label).
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static readonly Color Productive = ColorTranslator.FromHtml("#0ca30c");
    public static readonly Color Unproductive = ColorTranslator.FromHtml("#d03b3b");
    public static readonly Color Neutral = ColorTranslator.FromHtml("#a3a29e");
    public static readonly Color Offline = ColorTranslator.FromHtml("#6f6e6a");
    public static readonly Color Paused = ColorTranslator.FromHtml("#e0a63c");

    private static Icon? _icon;
    private static IntPtr _handle;

    public static Color ClassColor(LiveStatus s) => !s.Online ? Offline
        : s.Paused ? Paused
        : s.Class switch
        {
            "productive" => Productive,
            "unproductive" => Unproductive,
            _ => Neutral,
        };

    public static void Apply(NotifyIcon tray, LiveStatus s)
    {
        var color = ClassColor(s);
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            if (s.Paused && s.Online)
            {
                // amber pause glyph: distinct SHAPE, not just another colored dot — the state
                // is easy to forget and must not be mistaken for "neutral" at a glance
                using var brush = new SolidBrush(color);
                g.FillRectangle(brush, 4, 3, 3, 10);
                g.FillRectangle(brush, 9, 3, 3, 10);
            }
            else if (!s.Online || (s.Afk && !s.Active))
            {
                using var pen = new Pen(color, 2.5f);
                g.DrawEllipse(pen, 3, 3, 10, 10); // hollow = AFK / offline
            }
            else
            {
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, 2, 2, 12, 12);
            }
        }

        var newHandle = bmp.GetHicon();
        var newIcon = Icon.FromHandle(newHandle);
        tray.Icon = newIcon;
        tray.Text = Truncate("Minutar — " + s.Display, 63); // NotifyIcon tooltip hard limit

        // release the previous GDI icon AFTER the swap (GetHicon leaks otherwise)
        if (_handle != IntPtr.Zero)
        {
            _icon?.Dispose();
            DestroyIcon(_handle);
        }
        _icon = newIcon;
        _handle = newHandle;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
