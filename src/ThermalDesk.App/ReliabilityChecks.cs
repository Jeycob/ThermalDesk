using System.ComponentModel;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class ReliabilityChecks
{
    // Fake queries and task XML only: no native counters, task registration or elevation.
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Reliability: " + message); }
        double now = 0; int opens = 0;
        var first = new FakeQuery(); var second = new FakeQuery();
        using (var sampler = new GpuProcessSampler(() =>
        {
            opens++;
            if (opens == 1) throw new Win32Exception(5);
            return opens == 2 ? first : second;
        }, () => now))
        {
            Check(sampler.Read().Count == 0 && opens == 1 && sampler.Status.Contains("retry in 5s"), "initialization failure schedules retry");
            now = 4.99; sampler.Read(); Check(opens == 1, "repeated samples do not spin while unavailable");
            now = 5; Check(sampler.Read().Count == 0 && opens == 2 && sampler.Status.Contains("warming"), "retry initializes a fresh query and warms up");
            now = 6; Check(sampler.Read().Single().Pid == 42, "monitor recovers without a session restart");
            first.Fail = true; now = 7;
            Check(sampler.Read().Count == 0 && first.Disposals == 1 && sampler.Status.Contains("retry in 5s"), "query loss clears results, closes once, resets successful-session backoff");
            now = 11.99; sampler.Read(); Check(opens == 2, "query loss also respects backoff");
            now = 12; sampler.Read(); Check(opens == 3 && sampler.Read().Count == 1, "sampling failure recovers through a new query");
        }
        Check(first.Disposals == 1 && second.Disposals == 1, "every recovered query is disposed exactly once");
        opens = 0; now = 0;
        using (var unavailable = new GpuProcessSampler(() => { opens++; throw new Win32Exception(5); }, () => now))
        {
            foreach (int delay in new[] { 5, 10, 20, 40, 60, 60 })
            {
                unavailable.Read(); int before = opens; double due = now + delay;
                now = due - .01; unavailable.Read();
                Check(opens == before && unavailable.Status.Contains($"retry in {delay}s"), "persistent failure uses bounded exponential backoff");
                now = due;
            }
        }
        const string sid = "S-1-5-21-1-2-3-1001";
        const string task = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals><Principal id="Author"><UserId>S-1-5-21-1-2-3-1001</UserId><LogonType>InteractiveToken</LogonType></Principal></Principals>
              <Actions Context="Author"><Exec><Command>C:\Apps\ThermalDesk.exe</Command><Arguments>--startup --minimized</Arguments></Exec></Actions>
            </Task>
            """;
        Check(!StartupManager.ValidateExistingTask(null, sid, value => value), "absent task requires no ownership claim");
        Check(StartupManager.ValidateExistingTask(task, sid, value => value), "same-user application task can be changed");
        Check(StartupManager.ValidateExistingTask(task.Replace(sid, "DOMAIN\\Alice"), sid, value => value == "DOMAIN\\Alice" ? sid : ""), "account-name principal resolves to SID");
        void Refuses(string xml, string label)
        {
            bool rejected = false;
            try { StartupManager.ValidateExistingTask(xml, sid, value => value); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, label);
        }
        Refuses(task.Replace(sid, "S-1-5-21-1-2-3-1002"), "another user's sign-in task cannot be overwritten or removed");
        Refuses(task.Replace("<UserId>" + sid + "</UserId>", ""), "unidentifiable task owner blocks mutation");
        Refuses(task.Replace("ThermalDesk.exe", "Other.exe"), "foreign action is preserved even under the same owner");
        Refuses(task.Replace("</Actions>", "<Exec><Command>C:\\Other.exe</Command></Exec></Actions>"), "multiple custom actions are preserved");
        return checks;
    }

    private sealed class FakeQuery : IGpuProcessQuery
    {
        internal bool Fail;
        internal int Disposals;
        public IReadOnlyList<GpuEngineSample> Read() => Fail ? throw new Win32Exception(6) : [new(42, "3D", 75)];
        public void Dispose() => Disposals++;
    }
}
