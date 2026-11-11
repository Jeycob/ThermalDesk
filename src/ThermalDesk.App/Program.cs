using System.Text.Json;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--widget-check") return WidgetChecks.Run(Path.GetFullPath(args[1]));
        string[] checks = ["--sensor-check", "--fan-status-check", "--fan-curve-acceptance", "--aio-firmware-check", "--aio-display-check", "--panel-display-check"];
        if (args.Length == 2 && checks.Contains(args[0])) return HardwareChecks.Run(args[0], args[1]);
        if (args.Contains("--diagnose"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { devices = HidLink.Inventory(), conflictingProcesses = Engine.Conflicts() }, Settings.JsonOptions));
            return 0;
        }
        using var mutex = new Mutex(true, @"Local\ThermalDesk.SingleInstance", out bool created);
        if (!created) { MessageBox.Show("ThermalDesk is already running. Open its tray icon."); return 1; }
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermalDesk", "settings.json");
            // Preview never reads saved live configuration or opens hardware.
            var config = args.Contains("--render-preview") || args.Contains("--benchmark-preview") ? new Settings() : SettingsStore.Load(path);
            using var form = new MainForm(config, path);
            int benchmark = Array.IndexOf(args, "--benchmark-preview");
            if (benchmark >= 0)
            {
                if (benchmark + 1 >= args.Length) throw new ArgumentException("Specify a benchmark JSON path.");
                form.Benchmark(Path.GetFullPath(args[benchmark + 1])); return 0;
            }
            int preview = Array.IndexOf(args, "--render-preview");
            if (preview >= 0)
            {
                if (preview + 1 >= args.Length) throw new ArgumentException("Specify a preview PNG path.");
                form.RenderPreview(Path.GetFullPath(args[preview + 1])); return 0;
            }
            form.StartMinimized = args.Contains("--minimized");
            form.StartupRequested = StartupManager.ShouldStartLive(config, args, (Control.ModifierKeys & Keys.Shift) != 0);
            Application.Run(form);
            return 0;
        }
        catch (Exception e)
        {
            if (args.Contains("--render-preview") || args.Contains("--benchmark-preview")) Console.Error.WriteLine(e);
            else MessageBox.Show(e.ToString(), "ThermalDesk — startup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally { mutex.ReleaseMutex(); }
    }
}
