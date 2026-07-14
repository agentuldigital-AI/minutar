using System.Net.Http;
using System.Text.Json;

namespace Tracker.Supervisor;

/// <summary>Live classification state, polled from the daemon's GET /state every 2 s.</summary>
internal sealed record LiveStatus(string? Project, string Class, bool Afk, bool Active, bool Online, bool Focus = false)
{
    public string Label => !Online ? "offline"
        : Afk && !Active ? "AFK"
        : Class switch
        {
            "productive" => "Productiv",
            "unproductive" => "Neproductiv",
            "neutral" => "Neutru",
            _ => "—",
        };

    public string Display =>
        (Focus ? "🎯 " : "") + (string.IsNullOrEmpty(Project) ? Label : $"{Project} · {Label}");
}

internal sealed class StatusPoller : IDisposable
{
    private readonly HttpClient _http;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _busy;

    public LiveStatus Current { get; private set; } = new(null, "unknown", false, false, false);
    public event Action<LiveStatus>? Updated;

    public StatusPoller(string bridgeUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(bridgeUrl),
            Timeout = TimeSpan.FromMilliseconds(1500),
        };
        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
    }

    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var json = await _http.GetStringAsync("/state");
            using var doc = JsonDocument.Parse(json);
            var focusActive = doc.RootElement.TryGetProperty("focus", out var f)
                && f.ValueKind == JsonValueKind.Object
                && f.TryGetProperty("active", out var fa)
                && fa.ValueKind == JsonValueKind.True;
            if (doc.RootElement.TryGetProperty("engine", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                var project = e.TryGetProperty("project", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;
                var cls = e.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() ?? "neutral"
                    : "neutral";
                var afk = e.TryGetProperty("afk", out var a) && a.ValueKind == JsonValueKind.True;
                var active = e.TryGetProperty("active", out var ac) && ac.ValueKind == JsonValueKind.True;
                Current = new LiveStatus(project, cls, afk, active, true, focusActive);
            }
            else
            {
                // daemon up but watcher mirror stale — no classification available
                Current = new LiveStatus(null, "unknown", false, false, true, focusActive);
            }
        }
        catch
        {
            Current = new LiveStatus(null, "unknown", false, false, false);
        }
        finally
        {
            _busy = false;
        }
        Updated?.Invoke(Current);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
