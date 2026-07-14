using System.IO;
using System.Text.Json;

namespace Tracker.Supervisor;

/// <summary>Persisted UI preferences (machine state, NOT tracker.toml): %LOCALAPPDATA%\time-tracker\ui-state.json</summary>
internal sealed class UiState
{
    public bool MiniBarVisible { get; set; } = true;

    /// <summary>-1 = auto (docked left of the tray notification area).</summary>
    public int MiniBarOffsetX { get; set; } = -1;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "time-tracker", "ui-state.json");

    public static UiState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiState>(File.ReadAllText(FilePath)) ?? new UiState();
        }
        catch
        {
            // corrupt state file — fall back to defaults
        }
        return new UiState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // UI prefs are best-effort
        }
    }
}
