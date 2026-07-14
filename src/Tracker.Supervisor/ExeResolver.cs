using System.IO;
using Tracker.Shared.Config;

namespace Tracker.Supervisor;

/// <summary>Resolves component exe paths: config override → published layout → dev bin.</summary>
internal static class ExeResolver
{
    public static string AwServer(TrackerConfig cfg) =>
        cfg.Supervisor.AwServerExe.Length > 0
            ? cfg.Supervisor.AwServerExe
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "ActivityWatch", "aw-server", "aw-server.exe");

    public static string Component(TrackerConfig cfg, string project, string overridePath, string configPath)
    {
        if (overridePath.Length > 0) return overridePath;
        var exeName = project + ".exe";

        var published = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "time-tracker", "bin", project, exeName);
        if (File.Exists(published)) return published;

        var sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", project, exeName));
        if (File.Exists(sibling)) return sibling;

        // dev layout: <repo>/src/<project>/bin/Debug/net8.0-windows/<exe>
        var repo = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(configPath)))!;
        return Path.Combine(repo, "src", project, "bin", "Debug", "net8.0-windows", exeName);
    }
}
