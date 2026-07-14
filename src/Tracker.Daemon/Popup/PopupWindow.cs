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
    private static readonly Color CardBg        = Color.FromRgb(0x20, 0x20, 0x20);
    private static readonly Color CardEdge      = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
    private static readonly Color TextPrimary   = Color.FromArgb(0xE9, 0xFF, 0xFF, 0xFF);
    private static readonly Color TextSecondary = Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF);
    private static readonly Color BtnSubtle     = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);
    private static readonly Color BtnSubtleHov  = Color.FromArgb(0x21, 0xFF, 0xFF, 0xFF);
    private static readonly Color BtnEdge       = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
    private static readonly Color Green         = Color.FromRgb(0x23, 0x86, 0x36);
    private static readonly Color GreenHov      = Color.FromRgb(0x2E, 0xA0, 0x43);
    private static readonly Color Amber         = Color.FromRgb(0xFF, 0x9E, 0x57);
    private static readonly Color Track         = Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF);

    private readonly DispatcherTimer _topmostTimer;

    public PopupWindow(PopupModel model, PopupActions actions, Action onClosed)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false; // never steal foreground (see class doc)
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 20), MaxWidth = 480 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = model.CountdownSeconds is not null ? "🎯" : "⏱️",
            FontSize = 19,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = model.CountdownSeconds is not null ? "Focus mode — timp neproductiv" : "Timp neproductiv",
            FontSize = 15.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Frozen(TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(header);

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
            Text = model.ActivityText,
            FontSize = 13.5,
            Foreground = Frozen(TextPrimary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = model.StreakText,
            FontSize = 12,
            Foreground = Frozen(TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
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

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal };
        actionRow.Children.Add(MakePill("✓ Marchează productiv", Green, GreenHov, null, () =>
        {
            actions.MarkProductive();
            Close();
        }, bold: true));
        actionRow.Children.Add(MakePill($"Sunt sigur ({model.SureCooldownMinutes} min liniște)", BtnSubtle, BtnSubtleHov, BtnEdge, () =>
        {
            actions.Sure();
            Close();
        }));
        panel.Children.Add(actionRow);

        var card = new Border
        {
            Background = Frozen(CardBg),
            BorderBrush = Frozen(CardEdge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
            // room for the shadow inside the transparent window
            Margin = new Thickness(28),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black },
            RenderTransform = new TranslateTransform(0, 14),
            Opacity = 0,
        };
        Content = card;

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
    }

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
