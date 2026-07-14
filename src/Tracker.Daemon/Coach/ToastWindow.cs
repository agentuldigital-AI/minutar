using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Tracker.Daemon.Coach;

/// <summary>
/// Non-blocking coach toast (Coach v0): bottom-right corner, never steals focus,
/// auto-dismisses, optional click action. Deliberately calm — NOT the warn popup.
/// Same Fluent/Win11 card language as PopupWindow: rounded corners, drop shadow,
/// slide+fade entrance, Segoe UI Variable.
/// </summary>
public sealed class ToastWindow : Window
{
    private static readonly Color CardBg        = Color.FromRgb(0x20, 0x20, 0x20);
    private static readonly Color CardEdge      = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
    private static readonly Color TextPrimary   = Color.FromArgb(0xE9, 0xFF, 0xFF, 0xFF);
    private static readonly Color AccentBlue    = Color.FromRgb(0x7F, 0xB5, 0xFF);

    public ToastWindow(string title, string message, int seconds, Action? onClick)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 15), Width = 344 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Frozen(AccentBlue),
            Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13.5,
            Foreground = Frozen(TextPrimary),
            TextWrapping = TextWrapping.Wrap,
        });

        var card = new Border
        {
            Background = Frozen(CardBg),
            BorderBrush = Frozen(CardEdge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
            // room for the shadow inside the transparent window
            Margin = new Thickness(20),
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Direction = 270, Opacity = 0.45, Color = Colors.Black },
            RenderTransform = new TranslateTransform(24, 0),
            Opacity = 0,
        };
        Content = card;

        MouseLeftButtonUp += (_, _) =>
        {
            onClick?.Invoke();
            Close();
        };
        MouseRightButtonUp += (_, _) => Close();

        Loaded += (_, _) =>
        {
            // the card sits 20px inside the transparent window — offset so the visible
            // edge lands 14px from the screen corner
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth + 20 - 14;
            Top = wa.Bottom - ActualHeight + 20 - 14;

            // Fluent entrance: slide in from the right + fade, ~220ms decelerating
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(5, seconds)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // WS_EX_NOACTIVATE: never steal focus; TOOLWINDOW: out of Alt-Tab
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        _ = SetWindowLong(helper.Handle, GWL_EXSTYLE,
            GetWindowLong(helper.Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
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
