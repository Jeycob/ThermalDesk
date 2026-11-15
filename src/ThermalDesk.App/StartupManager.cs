using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class StartupManager
{
    private const string TaskName = "ThermalDesk-AtSignIn";
    private static readonly string TaskTool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
    internal static bool ShouldRequestElevation(string[] arguments, bool alreadyElevated)
    {
        // Offline software checks stay usable in unprivileged CI. These exact
        // two-argument modes return before loading any live hardware profile.
        bool offline = arguments.Length == 2 && arguments[0] is "--widget-check" or "--benchmark-display-loops" or "--render-preview" or "--benchmark-preview";
        return !alreadyElevated && !offline;
    }
    internal static ProcessStartInfo ElevationStartInfo(string executable, string[] arguments)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("An absolute application path is required.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    internal static bool ShouldStartLive(Settings settings, string[] arguments, bool shiftHeld, bool profileRecovered = false) =>
        settings.HasLiveSelection && !shiftHeld && !profileRecovered && !arguments.Contains("--preview") &&
        (arguments.Contains("--live-once") || (arguments.Contains("--startup") ? settings.StartLiveAtSignIn :
            (!arguments.Contains("--minimized") || settings.ResumeLastProfileOnLaunch == true) &&
            (settings.ResumeLastProfileOnLaunch ?? (settings.LastSessionWasLive || settings.StartLiveAtSignIn))));
    internal static bool ShouldStartMinimized(Settings settings, string[] arguments, bool profileRecovered, bool shiftHeld = false) =>
        !profileRecovered && !shiftHeld && !arguments.Contains("--show-window") &&
        (settings.MinimizeAfterStartup ?? arguments.Contains("--minimized"));
    internal static void RememberSuccessfulLive(Settings settings)
    {
        settings.LastSessionWasLive = true;
        settings.ResumeLastProfileOnLaunch ??= true;
    }
    internal static bool ShouldHideOnClose(bool enabled, CloseReason reason, bool exiting) =>
        enabled && reason == CloseReason.UserClosing && !exiting;
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
        string? task = ReadTaskXml();
        if (task is not null)
        {
            using var identity = WindowsIdentity.GetCurrent();
            try { ValidateExistingTask(task, identity.User!.Value, ResolveSid); }
            catch (InvalidOperationException) { return "Existing sign-in task is owned by another user or customized"; }
            return "Elevated sign-in task";
        }
        return key?.GetValue("ThermalDesk") is not null ? "Preview sign-in entry" : "Not registered";
    }
    private static string ResolveSid(string account) => account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
        ? new SecurityIdentifier(account).Value : ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;

    internal static bool ValidateExistingTask(string? taskXml, string currentSid, Func<string, string> resolveSid)
    {
        if (taskXml is null) return false;
        using var text = new StringReader(taskXml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
        var document = XDocument.Load(reader);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var principals = document.Root?.Element(ns + "Principals")?.Elements(ns + "Principal").ToArray() ?? [];
        string? owner = principals.Length == 1 ? (string?)principals[0].Element(ns + "UserId") : null;
        if (string.IsNullOrWhiteSpace(owner) || !resolveSid(owner).Equals(currentSid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The existing sign-in task belongs to another user or has no identifiable owner. Open ThermalDesk as its startup owner. Nothing was changed.");
        var actions = document.Root?.Element(ns + "Actions")?.Elements().ToArray() ?? [];
        string? command = actions.Length == 1 && actions[0].Name == ns + "Exec" ? (string?)actions[0].Element(ns + "Command") : null;
        if (string.IsNullOrWhiteSpace(command) || !Path.IsPathFullyQualified(command.Trim('"')) ||
            !Path.GetFileName(command.Trim('"')).Equals("ThermalDesk.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The existing sign-in task has custom actions. It was left unchanged; review it in Task Scheduler.");
        return true;
    }

    private static string? ReadTaskXml()
    {
        // COM exposes a stable file-not-found HRESULT; CLI exit codes/text cannot
        // distinguish an absent task from access denied on localized Windows.
        object? service = null, folder = null, task = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("Windows Task Scheduler is unavailable.");
            service = Activator.CreateInstance(type) ?? throw new IOException("Cannot connect to Windows Task Scheduler.");
            ((dynamic)service).Connect(); folder = ((dynamic)service).GetFolder(@"\");
            try { task = ((dynamic)folder).GetTask(TaskName); }
            catch (COMException error) when (error.HResult == unchecked((int)0x80070002)) { return null; }
            return (string)((dynamic)task).Xml;
        }
        catch (COMException error) { throw new IOException("The existing sign-in task could not be safely checked. Nothing was changed.", error); }
        finally
        {
            foreach (object? value in new[] { task, folder, service }) if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }
    public static void Configure(bool live)
    {
        if (live && !Elevated) throw new InvalidOperationException("Run ThermalDesk as administrator once to register the elevated live sign-in task. No protection or driver policy will be changed.");
        using var identity = WindowsIdentity.GetCurrent();
        bool exists = ValidateExistingTask(ReadTaskXml(), identity.User!.Value, ResolveSid);
        if (!live && exists)
        { var remove = Run("/Delete", "/TN", TaskName, "/F"); if (remove.Exit != 0) throw new IOException(remove.Output); }
        if (live)
        {
            string command = $"\"{Application.ExecutablePath}\" --startup --minimized";
            string[] arguments = ["/Create", "/TN", TaskName, "/SC", "ONLOGON", "/TR", command, "/RU", identity.Name, "/IT", "/RL", "HIGHEST"];
            var task = Run(exists ? [.. arguments, "/F"] : arguments);
            if (task.Exit != 0) throw new IOException("Could not register startup task: " + task.Output);
        }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (live) key.DeleteValue("ThermalDesk", false); else key.SetValue("ThermalDesk", $"\"{Application.ExecutablePath}\" --startup --preview --minimized");
    }
    public static void Remove()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (ValidateExistingTask(ReadTaskXml(), identity.User!.Value, ResolveSid))
        { var task = Run("/Delete", "/TN", TaskName, "/F"); if (task.Exit != 0) throw new IOException(task.Output); }
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
