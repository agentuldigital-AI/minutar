namespace Tracker.Shared.Aw;

/// <summary>
/// Bucket IDs and types (architecture §2). Window/AFK/web reuse the STOCK IDs so
/// aw-webui default views work unchanged (§5.1, awatcher precedent).
/// </summary>
public static class AwBuckets
{
    public const string WindowType = "currentwindow";
    public const string AfkType = "afkstatus";
    public const string WebType = "web.tab.current";
    public const string ProjectType = "tracker.project";
    public const string PausedType = "tracker.paused";
    public const string ClaudeWorkType = "claude.work";
    public const string ClaudeAttentionType = "claude.attention";

    public static string Window(string host) => $"aw-watcher-window_{host}";
    public static string Afk(string host) => $"aw-watcher-afk_{host}";
    public static string Web(string host) => $"aw-watcher-web_{host}";
    public static string Project(string host) => $"tracker-project_{host}";

    /// <summary>User-requested "stop tracking" windows — recorded so a pause reads as a
    /// pause in the reports instead of an unexplained gap.</summary>
    public static string Paused(string host) => $"tracker-paused_{host}";
    public static string ClaudeWork(string host) => $"claude-work_{host}";
    public static string ClaudeAttention(string host) => $"claude-attention_{host}";
}
