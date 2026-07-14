namespace Tracker.Shared.Config;

/// <summary>
/// Resolves the path to tracker.toml: --config arg → TIME_TRACKER_CONFIG env var →
/// walk up from the exe / cwd looking for config/tracker.toml (repo layout).
/// </summary>
public static class ConfigLocator
{
    public static string Resolve(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config")
                return Path.GetFullPath(args[i + 1]);
        }

        var env = Environment.GetEnvironmentVariable("TIME_TRACKER_CONFIG");
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);

        // instalarea standard (2026-07-14): config-ul locuiește în %LOCALAPPDATA%\TimeTracker,
        // decuplat de repo — git-ul nu mai calcă peste editările live, iar publicul n-are repo.
        // Dev-ul forțează repo-ul prin --config / env; fallback-ul pe repo rămâne mai jos.
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TimeTracker", "tracker.toml");
        if (File.Exists(appData)) return appData;

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "config", "tracker.toml");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent!;
            }
        }

        throw new FileNotFoundException(
            "tracker.toml not found. Pass --config <path>, set TIME_TRACKER_CONFIG, " +
            "or run from inside the repo (config/tracker.toml).");
    }
}
