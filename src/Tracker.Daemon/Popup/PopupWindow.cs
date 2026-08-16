using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Tracker.Daemon.Popup;

/// <summary>
/// Warn-only popup (decision #4), built in code (no XAML — the daemon has its own Main).
/// Fluent/Win11 card: rounded corners, drop shadow, slide+fade entrance, Segoe UI Variable.
/// NEVER activates (WS_EX_NOACTIVATE): if it took foreground, the watcher would report the
/// daemon's own process, the engine would classify it "neutral" and the controller would
/// auto-dismiss the popup ~1s after it appeared (the "disappears on mouse move" bug).
/// Always-on-top, re-asserted every 2 s while visible; exclusive-fullscreen games can
/// still cover it (accepted v1 limitation, research §3).
/// </summary>
public sealed class PopupWindow : Window
{
    private static readonly Color CardBg        = Color.FromRgb(0x28, 0x28, 0x2B);
    private static readonly Color CardTint      = Color.FromArgb(0x59, 0x1C, 0x1C, 0x20);
    private static readonly Color CardEdge      = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
    private static readonly Color TextPrimary   = Color.FromArgb(0xE9, 0xFF, 0xFF, 0xFF);
    private static readonly Color TextSecondary = Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF);
    private static readonly Color BtnSubtle     = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);
    private static readonly Color BtnSubtleHov  = Color.FromArgb(0x21, 0xFF, 0xFF, 0xFF);
    private static readonly Color BtnEdge       = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
    private static readonly Color Amber         = Color.FromRgb(0xFF, 0x9E, 0x57);
    private static readonly Color Track         = Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF);

    private readonly DispatcherTimer _topmostTimer;
    private readonly Border _card;

    public PopupWindow(PopupModel model, PopupActions actions, Action onClosed)
    {
        WindowStyle = WindowStyle.None;
        // NO AllowsTransparency: it forces WPF's own layered-window compositing, which makes
        // DWM refuse to draw a system backdrop. Rounded corners and the drop shadow come
        // from DWM instead (see OnSourceInitialized) — the same trade the mini-bar makes.
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false; // never steal foreground (see class doc)
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16), MaxWidth = 400 };

        // header row: icon + title on the left, close affordance pinned right
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            // no title next to it: the message below says what this is, in plain words
            Text = model.CountdownSeconds is not null ? "🎯" : "⏱️",
            FontSize = 17,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(header, 0);
        headerGrid.Children.Add(header);

        // "read it and move on" — no commitment, unlike the postpone buttons. Deliberately
        // quiet: it must be findable, not the first thing the eye lands on. During a focus
        // countdown it also counts as "any button cancels", so closing never closes the app.
        var closeBtn = MakePill("✕", BtnSubtle, BtnSubtleHov, BtnEdge, () =>
        {
            actions.Dismiss?.Invoke();
            Close();
        });
        closeBtn.Padding = new Thickness(9, 3, 9, 4);
        closeBtn.Margin = new Thickness(8, 0, 0, 0);
        closeBtn.VerticalAlignment = VerticalAlignment.Top;
        closeBtn.ToolTip = "Închide — fără nicio pauză promisă";
        Grid.SetColumn(closeBtn, 1);
        headerGrid.Children.Add(closeBtn);

        panel.Children.Add(headerGrid);

        if (model.CountdownSeconds is int cd)
        {
            var remaining = cd;
            var countdownText = new TextBlock
            {
                Text = $"Aplicația se închide în {remaining}s — orice buton anulează.",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Frozen(Amber),
                Margin = new Thickness(0, 0, 0, 6),
            };
            panel.Children.Add(countdownText);

            // slim countdown progress bar (empties as the deadline approaches)
            var fill = new Border { Background = Frozen(Amber), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
            var track = new Border
            {
                Background = Frozen(Track),
                CornerRadius = new CornerRadius(2),
                Height = 4,
                Margin = new Thickness(0, 0, 0, 12),
                Child = fill,
            };
            track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * remaining / cd;
            panel.Children.Add(track);

            var countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            countdownTimer.Tick += (_, _) =>
            {
                remaining--;
                if (remaining <= 0)
                {
                    countdownTimer.Stop();
                    Close(); // Closed → onClosed (no explicit action) fires re-nag; expiry runs the close
                    actions.OnCountdownExpired?.Invoke();
                    return;
                }
                countdownText.Text = $"Aplicația se închide în {remaining}s — orice buton anulează.";
                fill.Width = track.ActualWidth * remaining / cd;
            };
            countdownTimer.Start();
            Closed += (_, _) => countdownTimer.Stop();
        }

        panel.Children.Add(new TextBlock
        {
            Text = model.Message,
            FontSize = 16.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Frozen(TextPrimary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(new TextBlock
        {
            Text = model.ContextText,
            FontSize = 12,
            Foreground = Frozen(TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        var postponeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var minutes in model.PostponeOptionsMinutes)
        {
            var m = minutes;
            postponeRow.Children.Add(MakePill($"Amână {m} min", BtnSubtle, BtnSubtleHov, BtnEdge, () =>
            {
                actions.Postpone(m);
                Close();
            }));
        }
        panel.Children.Add(postponeRow);

        // "Marchează productiv" was removed on 2026-08-04 (user decision, amends locked
        // decision #8): a one-click escape hatch on the nag itself made it too easy to
        // reclassify an activity just to silence the popup. Marking still exists, but only
        // deliberately, from the dashboard's Settings page.
        // centred: it is wider than any single postpone pill, so left-aligned it left a
        // ragged gap on the right (user feedback, 2026-08-05)
        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var sureBtn = MakePill($"Sunt sigur ({model.SureCooldownMinutes} min liniște)", BtnSubtle, BtnSubtleHov, BtnEdge, () =>
        {
            actions.Sure();
            Close();
        });
        sureBtn.Margin = new Thickness(0); // MakePill's right margin would offset the centring
        actionRow.Children.Add(sureBtn);
        panel.Children.Add(actionRow);

        // The card itself no longer paints an opaque colour when the system backdrop is
        // available — that is what lets the acrylic show through. TrySystemBackdrop() fills
        // it back in when the backdrop is refused (Windows 10, transparency effects off,
        // low-end GPU), so the popup is never an unreadable transparent rectangle.
        _card = new Border
        {
            BorderBrush = Frozen(CardEdge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
            RenderTransform = new TranslateTransform(0, 14),
            Opacity = 0,
        };
        Content = _card;
        var card = _card;

        // Fluent entrance: slide up + fade in, ~220ms decelerating
        Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        };

        Closed += (_, _) =>
        {
            _topmostTimer!.Stop();
            onClosed();
        };

        // some apps steal topmost — re-assert while visible
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) =>
        {
            Topmost = false;
            Topmost = true;
        };
        _topmostTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // WS_EX_NOACTIVATE: clickable but never takes focus; TOOLWINDOW: out of Alt-Tab
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        _ = SetWindowLong(helper.Handle, GWL_EXSTYLE,
            GetWindowLong(helper.Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        ApplyBackdrop(helper.Handle);
    }

    /// <summary>
    /// Acrylic behind the popup, the way Windows 11 draws its own transient surfaces —
    /// Microsoft's guidance is explicit that flyouts and non-modal popups should use
    /// background acrylic, so the surface keeps a visual link to what triggered it.
    ///
    /// Every step is allowed to fail. Windows 10, "Transparency effects" turned off,
    /// Battery Saver and low-end GPUs all refuse the backdrop, and then a see-through card
    /// would be unreadable — so the card gets its solid colour back whenever the system
    /// says no. That fallback is the normal path on anything older than Windows 11 22H2.
    /// </summary>
    private void ApplyBackdrop(IntPtr hwnd)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        const int DWMWCP_ROUND = 2;
        const int DWMSBT_TRANSIENTWINDOW = 3; // the acrylic flavour meant for popups/flyouts

        var dark = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        var round = DWMWCP_ROUND;
        var rounded = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) == 0;

        var backdrop = DWMSBT_TRANSIENTWINDOW;
        var acrylic = TransparencyEnabled()
            && DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;

        // Even over acrylic the card keeps a thin tint: DWM blurs what is behind, but a very
        // bright window underneath would still wash out the text. This is the same idea as
        // acrylic's own tint layer — enough for contrast, not enough to kill the blur.
        _card.Background = Frozen(acrylic ? CardTint : CardBg);
        // without native rounding the card must clip its own corners, or the square window
        // edge shows through behind the rounded border
        if (!rounded) _card.CornerRadius = new CornerRadius(0);
    }

    /// <summary>User setting "Transparency effects" — DWM would give us a flat colour anyway,
    /// but asking first keeps the fallback explicit instead of accidental.</summary>
    private static bool TransparencyEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch
        {
            return true; // unreadable setting is not a reason to look worse
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Buttons are hand-rolled Borders: full control over hover/rounding without ControlTemplates.</summary>
    private static Border MakePill(string text, Color bg, Color hoverBg, Color? edge, Action onClick, bool bold = false)
    {
        var pill = new Border
        {
            Background = Frozen(bg),
            CornerRadius = new CornerRadius(6),
            BorderBrush = edge is { } c ? Frozen(c) : null,
            BorderThickness = new Thickness(edge is null ? 0 : 1),
            Padding = new Thickness(13, 7, 13, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Frozen(TextPrimary),
            },
        };
        pill.MouseEnter += (_, _) => pill.Background = Frozen(hoverBg);
        pill.MouseLeave += (_, _) => pill.Background = Frozen(bg);
        pill.MouseLeftButtonUp += (_, _) => onClick();
        return pill;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
