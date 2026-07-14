using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tracker.Supervisor;

/// <summary>
/// Tiny always-on-top status bar docked OVER the taskbar (TrafficMonitor pattern —
/// Win11 removed the DeskBand API, so integrated toolbars are no longer possible):
/// "● project · class" live. Fluent/Win11 pill: DWM-rounded corners, class-colored
/// border + dot glow (gentle pulse while unproductive), two-tone text, hover feedback.
/// Never covers the workspace (it sits on the taskbar), auto-hides in fullscreen /
/// presentation mode, draggable horizontally (position saved), click = open dashboard,
/// right-click = the supervisor tray menu.
/// </summary>
internal sealed class MiniBarForm : Form
{
    private const int BarHeight = 26;
    private const int PadX = 10;
    private const int CornerRadius = 8; // matches DWMWCP_ROUND (~8px on Win11)

    private static readonly Color BgBase = Color.FromArgb(0x20, 0x20, 0x20);
    private static readonly Color BgHover = Color.FromArgb(0x2d, 0x2d, 0x2d);
    private static readonly Color TextPrimary = Color.FromArgb(0xf0, 0xf0, 0xf0);
    private static readonly Color TextSecondary = Color.FromArgb(0x9a, 0x9a, 0x9a);

    private const TextFormatFlags TextFlags =
        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private readonly UiState _state;
    private readonly Action _onClick;
    private readonly Func<Task<string?>>? _tooltipProvider;
    private LiveStatus _status = new(null, "unknown", false, false, false);
    private readonly System.Windows.Forms.Timer _dockTimer;
    private readonly System.Windows.Forms.Timer _pulseTimer;
    private readonly Font _fontPrimary = MakePrimaryFont();
    private readonly Font _fontSecondary = MakeSecondaryFont();
    private readonly ToolTip _tip = new();
    private DateTime _tipFetchedAt = DateTime.MinValue;

    private bool _dragging;
    private int _dragStartX;
    private int _formStartX;
    private bool _moved;

    public MiniBarForm(UiState state, ContextMenuStrip menu, Action onClick, Func<Task<string?>>? tooltipProvider = null)
    {
        _state = state;
        _onClick = onClick;
        _tooltipProvider = tooltipProvider;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        // custom paint (rounded border, glow, two text runs) + pulse timer NEED
        // double buffering, otherwise every Invalidate erases the background first
        // and the bar visibly flickers on each poll/pulse tick
        DoubleBuffered = true;
        BackColor = BgBase;
        ForeColor = TextPrimary;
        ContextMenuStrip = menu;
        Height = BarHeight;
        Width = 170;
        Cursor = Cursors.Hand;
        _tip.SetToolTip(this, "Minutar — click deschide dashboard-ul");

        MouseEnter += (_, _) =>
        {
            BackColor = BgHover;
            RefreshTooltipAsync();
        };
        MouseLeave += (_, _) => BackColor = BgBase;

        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _moved = false;
            _dragStartX = Cursor.Position.X;
            _formStartX = Left;
        };
        MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            var dx = Cursor.Position.X - _dragStartX;
            if (Math.Abs(dx) > 3) _moved = true;
            if (_moved) Left = _formStartX + dx;
        };
        MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = false;
            if (_moved)
            {
                var (bar, _) = TaskbarInterop.GetRects();
                _state.MiniBarOffsetX = Left - bar.Left;
                _state.Save();
            }
            else
            {
                _onClick();
            }
        };

        // gentle glow pulse while on an unproductive activity (peripheral signal)
        _pulseTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _pulseTimer.Tick += (_, _) => Invalidate();

        _dockTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _dockTimer.Tick += (_, _) => Redock();
        _dockTimer.Start();
        Redock();
    }

    // never steal focus from the app the user is working in
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Win11 pill look; silently a no-op on Win10 (attribute unsupported)
        var pref = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
    }

    public void UpdateStatus(LiveStatus status)
    {
        var changed = status != _status; // record value-equality
        _status = status;
        var pulse = status.Online && status.Class == "unproductive" && !(status.Afk && !status.Active);
        _pulseTimer.Enabled = pulse && Visible;
        if (!changed) return; // identical poll result — nothing to repaint (pulse timer redraws on its own)

        var (primary, secondary) = SplitDisplay(status);
        var w = PadX + 16 + Measure(primary, _fontPrimary);
        if (secondary.Length > 0) w += Measure(secondary, _fontSecondary);
        var newWidth = Math.Min(320, w + PadX);
        if (Math.Abs(newWidth - Width) > 4)
        {
            Width = newWidth;
            Redock();
        }
        Invalidate();
    }

    /// <summary>Immediate visibility/position refresh (tray menu toggle).</summary>
    public void RedockNow() => Redock();

    /// <summary>Keeps the bar glued to the taskbar and hidden during fullscreen apps.</summary>
    private void Redock()
    {
        var fullscreen = TaskbarInterop.IsFullscreenContext();
        var shouldShow = _state.MiniBarVisible && !fullscreen;
        if (Visible != shouldShow) Visible = shouldShow;
        if (!shouldShow)
        {
            _pulseTimer.Enabled = false;
            return;
        }

        var (bar, tray) = TaskbarInterop.GetRects();
        if (bar.Width <= 0) return;

        var y = bar.Top + (bar.Height - Height) / 2;
        var x = _state.MiniBarOffsetX >= 0
            ? bar.Left + _state.MiniBarOffsetX
            : (tray.Width > 0 ? tray.Left - Width - 8 : bar.Right - Width - 220);
        x = Math.Max(bar.Left, Math.Min(x, bar.Right - Width));
        Location = new Point(x, y);
        TopMost = true; // re-assert (explorer restarts, z-order churn)
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var color = TrayIconFactory.ClassColor(_status);

        // border tinted by the current class (matches the DWM-rounded window shape)
        using (var path = RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), CornerRadius))
        using (var border = new Pen(Color.FromArgb(0x70, color)))
        {
            g.DrawPath(border, path);
        }

        var hollow = !_status.Online || (_status.Afk && !_status.Active);
        var dotY = Height / 2 - 4;
        if (hollow)
        {
            using var pen = new Pen(color, 2f);
            g.DrawEllipse(pen, PadX, dotY, 8, 8);
        }
        else
        {
            // soft halo behind the dot; breathes while unproductive
            var glowAlpha = 0x28;
            if (_pulseTimer.Enabled)
                glowAlpha = (int)(0x18 + 0x30 * (0.5 + 0.5 * Math.Sin(Environment.TickCount / 260.0)));
            using (var glow = new SolidBrush(Color.FromArgb(glowAlpha, color)))
            {
                g.FillEllipse(glow, PadX - 4, dotY - 4, 16, 16);
            }
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, PadX, dotY, 8, 8);
        }

        // two-tone text: project stands out, the class label recedes
        var (primary, secondary) = SplitDisplay(_status);
        var x = PadX + 16;
        var pw = Math.Min(Measure(primary, _fontPrimary), Width - x - PadX);
        TextRenderer.DrawText(g, primary, _fontPrimary, new Rectangle(x, 0, pw, Height), TextPrimary, TextFlags);
        x += pw;
        if (secondary.Length > 0 && x < Width - PadX)
            TextRenderer.DrawText(g, secondary, _fontSecondary, new Rectangle(x, 0, Width - x - PadX, Height), TextSecondary, TextFlags);
    }

    private static (string Primary, string Secondary) SplitDisplay(LiveStatus s)
    {
        var prefix = s.Focus ? "🎯 " : "";
        return string.IsNullOrEmpty(s.Project)
            ? (prefix + s.Label, "")
            : (prefix + s.Project, " · " + s.Label);
    }

    private static int Measure(string text, Font font) =>
        TextRenderer.MeasureText(text, font, new Size(int.MaxValue, BarHeight), TextFlags).Width;

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Rich tooltip (today's class totals + focus score), cached 30 s.</summary>
    private async void RefreshTooltipAsync()
    {
        if (_tooltipProvider is null || DateTime.UtcNow - _tipFetchedAt < TimeSpan.FromSeconds(30)) return;
        try
        {
            var text = await _tooltipProvider();
            if (!string.IsNullOrEmpty(text))
            {
                _tipFetchedAt = DateTime.UtcNow;
                _tip.SetToolTip(this, text);
            }
        }
        catch
        {
            // daemon unreachable — keep the previous tooltip
        }
    }

    private static Font MakePrimaryFont()
    {
        // true SemiBold when Segoe UI Variable is present (Win11); otherwise simulated bold
        foreach (var name in new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" })
        {
            var f = new Font(name, 9f);
            if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
        }
        return new Font("Segoe UI", 9f, FontStyle.Bold);
    }

    private static Font MakeSecondaryFont()
    {
        var f = new Font("Segoe UI Variable Text", 9f);
        if (f.Name.StartsWith("Segoe UI Variable", StringComparison.OrdinalIgnoreCase)) return f;
        f.Dispose();
        return new Font("Segoe UI", 9f);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dockTimer.Dispose();
            _pulseTimer.Dispose();
            _fontPrimary.Dispose();
            _fontSecondary.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Taskbar geometry + fullscreen detection (SHQueryUserNotificationState).</summary>
internal static class TaskbarInterop
{
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public static (Rectangle Taskbar, Rectangle Tray) GetRects()
    {
        var bar = Rectangle.Empty;
        var tray = Rectangle.Empty;
        var hBar = FindWindow("Shell_TrayWnd", null);
        if (hBar != IntPtr.Zero && GetWindowRect(hBar, out var rb))
            bar = Rectangle.FromLTRB(rb.Left, rb.Top, rb.Right, rb.Bottom);
        var hTray = hBar != IntPtr.Zero ? FindWindowEx(hBar, IntPtr.Zero, "TrayNotifyWnd", null) : IntPtr.Zero;
        if (hTray != IntPtr.Zero && GetWindowRect(hTray, out var rt))
            tray = Rectangle.FromLTRB(rt.Left, rt.Top, rt.Right, rt.Bottom);
        return (bar, tray);
    }

    /// <summary>True while a fullscreen game/video/presentation owns the screen.</summary>
    public static bool IsFullscreenContext()
    {
        // QUNS: 1=not present, 2=busy, 3=D3D fullscreen, 4=presentation, 5=accepts, 6=quiet, 7=app
        return SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4;
    }
}
