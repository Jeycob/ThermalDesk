using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class StartupManager
{
    private const string TaskName = "ThermalDesk-AtSignIn";
    private static readonly string TaskTool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
    internal static bool ShouldStartLive(Settings settings, string[] arguments, bool shiftHeld, bool profileRecovered = false) =>
        !shiftHeld && !profileRecovered && (arguments.Contains("--live-once") || settings.StartLiveAtSignIn && arguments.Contains("--startup"));
    internal static bool ShouldStartMinimized(string[] arguments, bool profileRecovered) => arguments.Contains("--minimized") && !profileRecovered;
    internal static void WaitForPrevious(string[] arguments)
    {
        int index = Array.IndexOf(arguments, "--wait-for-process");
        if (index < 0) return;
        if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out int id) || id <= 0 || id == Environment.ProcessId)
            throw new ArgumentException("Invalid restart process ID.");
        Process previous;
        try { previous = Process.GetProcessById(id); } catch (ArgumentException) { return; }
        using (previous)
        {
            if (!previous.ProcessName.Equals("ThermalDesk", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Restart target is not ThermalDesk.");
            if (!previous.WaitForExit(20000)) throw new TimeoutException("Previous ThermalDesk is still shutting down. Do not start a second controller.");
        }
    }
    public static bool Elevated
    {
        get { using var user = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator); }
    }
    public static string State()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return ExistsTask() ? "Elevated sign-in task" : key?.GetValue("ThermalDesk") is not null ? "Preview sign-in entry" : "Not registered";
    }
    private static bool ExistsTask() => Run("/Query", "/TN", TaskName).Exit == 0;
    public static void Configure(bool live)
    {
        if (live && !Elevated) throw new InvalidOperationException("Run ThermalDesk as administrator once to register the elevated live sign-in task. No protection or driver policy will be changed.");
        if (!live && ExistsTask())
        { var remove = Run("/Delete", "/TN", TaskName, "/F"); if (remove.Exit != 0) throw new IOException(remove.Output); }
        if (live)
        {
            using var identity = WindowsIdentity.GetCurrent();
            string command = $"\"{Application.ExecutablePath}\" --startup --minimized";
            var task = Run("/Create", "/TN", TaskName, "/SC", "ONLOGON", "/TR", command, "/RU", identity.Name, "/IT", "/RL", "HIGHEST", "/F");
            if (task.Exit != 0) throw new IOException("Could not register startup task: " + task.Output);
        }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (live) key.DeleteValue("ThermalDesk", false); else key.SetValue("ThermalDesk", $"\"{Application.ExecutablePath}\" --minimized");
    }
    public static void Remove()
    {
        if (ExistsTask()) { var task = Run("/Delete", "/TN", TaskName, "/F"); if (task.Exit != 0) throw new IOException(task.Output); }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); key.DeleteValue("ThermalDesk", false);
    }
    private static (int Exit, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(TaskTool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in arguments) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Task Scheduler CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(); throw new IOException("Task Scheduler CLI timed out."); }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
