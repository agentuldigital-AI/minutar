using System.Windows;
using System.Windows.Threading;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Popup;

public sealed record PopupModel(
    string ActivityText,
    string StreakText,
    IReadOnlyList<int> PostponeOptionsMinutes,
    int SureCooldownMinutes,
    int? CountdownSeconds = null);

public sealed record PopupActions(
    Action<int> Postpone,
    Action MarkProductive,
    Action Sure,
    Action? OnCountdownExpired = null);

/// <summary>
/// Hosts WPF on a dedicated STA thread (the daemon's main thread runs Kestrel).
/// Show/Hide are marshalled onto the WPF dispatcher.
/// </summary>
public sealed class PopupService
{
    private readonly ManualResetEventSlim _ready = new();
    private Dispatcher? _dispatcher;
    private PopupWindow? _window;

    public bool IsVisible { get; private set; }

    public void Start()
    {
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            app.Run();
        })
        {
            IsBackground = true,
            Name = "wpf-popup",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));
        Log.Info("Popup service started (WPF STA thread)");
    }

    public void Show(PopupModel model, PopupActions actions, Action? onClosed = null)
    {
        _dispatcher?.BeginInvoke(() =>
        {
            _window?.Close();
            _window = new PopupWindow(model, actions, onClosed: () =>
            {
                IsVisible = false;
                onClosed?.Invoke();
            });
            // no Activate(): the popup must never take foreground — the watcher would
            // report the daemon's own process and the controller would auto-dismiss it
            _window.Show();
            IsVisible = true;
        });
    }

    public void Hide()
    {
        _dispatcher?.BeginInvoke(() =>
        {
            _window?.Close();
            _window = null;
            IsVisible = false;
        });
    }

    /// <summary>Coach v0: calm, non-activating toast in the bottom-right corner.</summary>
    public void ShowToast(string title, string message, int seconds, Action? onClick = null)
    {
        _dispatcher?.BeginInvoke(() =>
        {
            new Coach.ToastWindow(title, message, seconds, onClick).Show();
        });
    }
}
