using System.Windows.Forms;
using Tracker.Shared.Config;
using Tracker.Shared.Logging;

namespace Tracker.Supervisor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Log.Init("supervisor");
        var uiTest = args.Contains("--ui-test"); // tray+minibar only, no children, own mutex
        using var mutex = new Mutex(
            initiallyOwned: true,
            uiTest ? "Global\\TimeTrackerSupervisorUiTest" : "Global\\TimeTrackerSupervisor",
            out var createdNew);
        if (!createdNew)
        {
            Log.Warn("Supervisor already running — exiting.");
            return;
        }

        try
        {
            var cfgPath = ConfigLocator.Resolve(args);
            var cfg = TrackerConfig.Load(cfgPath);
            Log.Info($"Config loaded from {cfgPath}");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(cfg, cfgPath, uiTest));
        }
        catch (Exception ex)
        {
            Log.Error("Fatal: " + ex);
        }
    }
}
