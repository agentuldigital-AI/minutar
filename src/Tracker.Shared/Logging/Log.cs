namespace Tracker.Shared.Logging;

/// <summary>
/// Tiny thread-safe file + console logger. Files go to
/// %LOCALAPPDATA%\time-tracker\logs\{component}-{yyyy-MM-dd}.log — never inside the repo.
/// </summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string _component = "tracker";
    private static string? _logDir;

    public static void Init(string component)
    {
        _component = component;
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "time-tracker", "logs");
        Directory.CreateDirectory(_logDir);
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Lock)
        {
            Console.WriteLine(line);
            if (_logDir is null) return;
            try
            {
                var file = Path.Combine(_logDir, $"{_component}-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // logging must never crash a component (any failure kind, not just IO)
            }
        }
    }
}
