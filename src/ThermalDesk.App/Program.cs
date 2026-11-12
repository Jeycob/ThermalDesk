using System.Text.Json;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try { StartupManager.WaitForPrevious(args); }
        catch (Exception error) { MessageBox.Show(error.Message, "ThermalDesk restart blocked"); return 1; }
        if (args.Length == 2 && args[0] == "--widget-check") return WidgetChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--benchmark-display-loops") return WidgetChecks.BenchmarkDisplayLoops(Path.GetFullPath(args[1]));
        string[] checks = ["--log-check", "--process-check", "--sensor-check", "--fan-status-check", "--fan-curve-acceptance", "--controller-stability-check", "--controller-profile-check", "--aio-firmware-check", "--aio-display-check", "--panel-display-check"];
        if (args.Length == 2 && checks.Contains(args[0])) return HardwareChecks.Run(args[0], args[1]);
        if (args.Contains("--diagnose"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { devices = HidLink.Inventory(), conflictingProcesses = Engine.Conflicts() }, Settings.JsonOptions));
            return 0;
        }
        if (StartupManager.ShouldRequestElevation(args, StartupManager.Elevated))
        {
            // Ask Windows for consent before reading the profile or creating a UI.
            // Elevation preserves the requested launch mode. --preview and Shift
            // always bypass saved auto-resume; defaults are never silently armed.
            try
            {
                using var elevated = System.Diagnostics.Process.Start(StartupManager.ElevationStartInfo(Application.ExecutablePath, args))
                    ?? throw new IOException("Windows did not launch the administrator instance.");
                return 0;
            }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                MessageBox.Show("Administrator access was not approved. ThermalDesk was not started; no hardware control was enabled.", "ThermalDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            catch (Exception error)
            {
                MessageBox.Show("Could not start ThermalDesk as administrator. No limited-access or live fallback was started.\n\n" + error.Message, "ThermalDesk", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        using var mutex = new Mutex(true, @"Local\ThermalDesk.SingleInstance", out bool created);
        if (!created) { MessageBox.Show("ThermalDesk is already running. Open its tray icon."); return 1; }
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermalDesk", "settings.json");
            // Preview never reads saved live configuration or opens hardware.
            var loaded = args.Contains("--render-preview") || args.Contains("--benchmark-preview")
                ? new StartupProfile(new Settings(), false, "", null) : SettingsStore.LoadForStartup(path);
            var config = loaded.Settings;
            using var form = new MainForm(config, path, startupWarning: loaded.Warning);
            int status = Array.IndexOf(args, "--status-output");
            if (status >= 0)
            {
                if (status + 1 >= args.Length) throw new ArgumentException("Specify an acceptance status JSON path.");
                form.AcceptanceStatusPath = Path.GetFullPath(args[status + 1]);
            }
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
            form.StartMinimized = StartupManager.ShouldStartMinimized(config, args, loaded.Recovered, (Control.ModifierKeys & Keys.Shift) != 0);
            form.StartupRequested = StartupManager.ShouldStartLive(config, args, (Control.ModifierKeys & Keys.Shift) != 0, loaded.Recovered);
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
