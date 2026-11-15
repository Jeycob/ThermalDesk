using System.Collections.Concurrent;
using System.Diagnostics;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// Injected telemetry/controllers only. No USB, driver, process or profile access.
internal static class ControllerReliabilityChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value, string name) { count++; if (!value) throw new InvalidOperationException("Controller reliability: " + name); }
        bool Wait(Func<bool> value, int milliseconds = 5000) => SpinWait.SpinUntil(value, milliseconds);
        static Settings Profile(int ports = 1)
        {
            var s = new Settings { CoolingEnabled = true, CoolingWiringConfirmed = true, PollSeconds = 1 };
            foreach (var p in s.Ports.Take(ports)) { p.Role = PortRole.Fan; p.Mode = ControlMode.Fixed; p.FixedDuty = 40; }
            return s;
        }
        static Telemetry Sample(double cpu = 50) => new(DateTimeOffset.UtcNow, cpu, 40, 0, 0, 30);
        static void Stop(Engine e) => e.Stop().GetAwaiter().GetResult();

        var tracker = new Engine.DutyConfirmation(70, 0);
        Check(!tracker.Observe(1, 40, 70, 1), "delayed readback gets a finite grace period");
        Check(tracker.Observe(1, 40, 70, 1.5) && tracker.Retries == 1, "first mismatch retries after grace");
        Check(!tracker.Observe(1, 69, 70, 2) && tracker.ConfirmedDuty == 69 && tracker.Retries == 0, "quantized matching readback confirms and clears retry state");
        tracker.Command(100, 3);
        Check(!tracker.Observe(1, 70, 70, 3.1) && tracker.Description.Contains("awaiting"), "pre-emergency readback cannot confirm a newly sent emergency command");
        tracker.Command(90, 4);
        Check(tracker.Observe(1, 40, 90, 4.5), "changing an unconfirmed target does not restart its grace");
        tracker.Command(80, 5);
        Check(tracker.Observe(1, 40, 80, 6), "moving target retains bounded retry count");
        tracker.Command(75, 7);
        bool rejected = false;
        try { tracker.Observe(1, 40, 75, 7.5); } catch (IOException) { rejected = true; }
        Check(rejected, "continuously changing unconfirmed targets eventually latch a fault");

        var ignored = new FakeController { IgnoreCommands = int.MaxValue };
        var missingAck = new Engine(_ => ignored, () => Sample(90), () => []);
        try
        {
            missingAck.Start(Profile(), true);
            Check(Wait(() => missingAck.ControllerFault, 8500), "nonzero RPM cannot hide an ignored emergency duty command");
            Check(ignored.Writes.Count == 3 && ignored.Writes.All(w => w.Duty == 100), "ignored command receives exactly two bounded retries");
            Check(missingAck.Status["Controller"].Contains("duty not confirmed") && missingAck.Logs.Any(x => x.Contains("confirmation grace")), "fault and retries explain the requested/readback mismatch and grace");
        }
        finally { Stop(missingAck); }

        var delayed = new FakeController { IgnoreCommands = 1 };
        var converging = new Engine(_ => delayed, () => Sample(90), () => []);
        try
        {
            converging.Start(Profile(), true);
            Check(Wait(() => delayed.Writes.Count >= 2 && converging.Fans.TryGetValue(1, out var f) && f.Duty == 100), "a transient ignored command converges after retry");
            Check(!converging.ControllerFault && delayed.Writes.Count == 2, "successful confirmation avoids extra retries or reconnects");
        }
        finally { Stop(converging); }

        var expiring = new FakeController { ReadDelayMilliseconds = 650, IgnoreCommands = int.MaxValue };
        var expireSettings = Profile(); expireSettings.PollSeconds = 2; expireSettings.Ports[0].FixedDuty = 70;
        var expiredRead = new Engine(_ => expiring, () => Sample() with { Time = DateTimeOffset.UtcNow.AddSeconds(-4.6) }, () => []);
        try
        {
            expiredRead.Start(expireSettings, true);
            Check(Wait(() => expiring.ReadPorts.Count > 0), "feedback read starts while the sensor sample is still valid");
            Check(Wait(() => expiredRead.ControllerFault, 1500), "a sample expiring inside a read faults before the next two-second control deadline");
            Check(expiring.Writes.Count == 1 && expiredRead.Status["Controller"].Contains("Stale telemetry"), "expired telemetry never authorizes a readback retry");
        }
        finally { Stop(expiredRead); }

        using var baselineEntered = new ManualResetEventSlim();
        using var baselineRelease = new ManualResetEventSlim();
        int hot = 0;
        var starting = new FakeController { OnBaseline = _ => { baselineEntered.Set(); baselineRelease.Wait(5000); } };
        var startup = new Engine(_ => starting, () => Sample(Volatile.Read(ref hot) == 0 ? 50 : 90), () => []);
        try
        {
            startup.Start(Profile(), true);
            Check(baselineEntered.Wait(3000), "initial baseline read is explicitly separated from the speed write");
            Volatile.Write(ref hot, 1);
            Check(Wait(() => startup.Latest.CpuTemperature == 90), "new critical sample arrives during blocked baseline read");
            baselineRelease.Set();
            Check(Wait(() => starting.Writes.Count > 0) && starting.Writes.First().Duty == 100, "first write uses the fresh critical sample after baseline completes");
        }
        finally { baselineRelease.Set(); Stop(startup); }

        using var laterBaselineEntered = new ManualResetEventSlim();
        using var laterBaselineRelease = new ManualResetEventSlim();
        int laterHot = 0; bool earlierProtected = false;
        var multiStart = new FakeController();
        multiStart.OnBaseline = port =>
        {
            if (port == 2) { laterBaselineEntered.Set(); laterBaselineRelease.Wait(5000); }
            if (port == 3) earlierProtected = multiStart.Writes.Last(w => w.Port == 1).Duty == 100;
        };
        var multiStartup = new Engine(_ => multiStart, () => Sample(Volatile.Read(ref laterHot) == 0 ? 50 : 90), () => []);
        try
        {
            multiStartup.Start(Profile(3), true);
            Check(laterBaselineEntered.Wait(3000), "second port baseline starts after the first port is controlled");
            Volatile.Write(ref laterHot, 1);
            Check(Wait(() => multiStartup.Latest.CpuTemperature == 90), "critical sample arrives during a later baseline capture");
            laterBaselineRelease.Set();
            Check(Wait(() => multiStart.Writes.Any(w => w.Port == 3)) && earlierProtected, "already controlled ports receive emergency duty before another baseline read");
        }
        finally { laterBaselineRelease.Set(); Stop(multiStartup); }

        using var cancelEntered = new ManualResetEventSlim();
        using var cancelRelease = new ManualResetEventSlim();
        var cancelled = new FakeController { OnBaseline = _ => { cancelEntered.Set(); cancelRelease.Wait(5000); } };
        var cancelling = new Engine(_ => cancelled, () => Sample(), () => []);
        cancelling.Start(Profile(), true);
        try
        {
            Check(cancelEntered.Wait(3000), "cancellation fixture reached the baseline read");
            var stopping = cancelling.Stop(); cancelRelease.Set(); stopping.GetAwaiter().GetResult();
            Check(cancelled.Writes.IsEmpty && !cancelling.ControllerFault, "Stop during baseline performs no speed write and invents no fault");
        }
        finally { cancelRelease.Set(); Stop(cancelling); }

        var steady = new FakeController { ReadDelayMilliseconds = 650 };
        var cadence = new Engine(_ => steady, () => Sample(), () => []);
        try
        {
            var times = new List<DateTimeOffset>();
            cadence.Start(Profile(5), true);
            Check(Wait(() =>
            {
                if (cadence.FanCommands.TryGetValue(1, out var c) && (times.Count == 0 || times[^1] != c.Time)) times.Add(c.Time);
                return times.Count >= 4 || cadence.ControllerFault;
            }, 6500) && !cadence.ControllerFault && times.Count >= 4, "normal control continues inside slow five-port feedback sweeps");
            Check(times.Zip(times.Skip(1), (a, b) => (b - a).TotalSeconds).All(gap => gap < 1.9), "one-second control deadline adds at most one bounded transaction, not a full read sweep");
            Check(steady.MaximumConcurrency == 1, "normal control and feedback preserve serial transport ownership");
        }
        finally { Stop(cadence); }

        int changed = 0, reads = 0;
        var slow = new FakeController { ReadDelayMilliseconds = 650 };
        slow.OnRead = _ => { if (Interlocked.Increment(ref reads) == 1) Volatile.Write(ref changed, 1); };
        var responsive = new Engine(_ => slow, () => Sample(Volatile.Read(ref changed) == 0 ? 50 : 90), () => []);
        try
        {
            responsive.Start(Profile(5), true);
            Check(Wait(() => responsive.Latest.CpuTemperature == 90), "critical sample arrives during a five-port slow feedback sweep");
            long observed = Stopwatch.GetTimestamp();
            Check(Wait(() => slow.Writes.Count(w => w.Duty == 100) == 5, 2200), "all five emergency commands preempt the remaining slow feedback reads");
            double responseSeconds = Stopwatch.GetElapsedTime(observed).TotalSeconds;
            Check(responseSeconds < 1.5 && slow.ReadPorts.Count < 5 && !responsive.ControllerFault, "emergency waits for at most one bounded read, not the entire sweep");
            Check(slow.MaximumConcurrency == 1, "read, speed and lighting transactions keep a single serial owner");
        }
        finally { Stop(responsive); }
        return count;
    }

    private sealed class FakeController : IFanController
    {
        internal readonly ConcurrentQueue<(int Port, int Duty)> Writes = new();
        internal readonly ConcurrentQueue<int> ReadPorts = new();
        private readonly Dictionary<int, int> duties = [];
        internal int IgnoreCommands, ReadDelayMilliseconds, MaximumConcurrency;
        private int active;
        internal Action<int>? OnBaseline, OnRead;
        private void Enter() { int n = Interlocked.Increment(ref active); MaximumConcurrency = Math.Max(MaximumConcurrency, n); }
        private void Exit() => Interlocked.Decrement(ref active);
        public void CaptureBaseline(int port) { Enter(); try { OnBaseline?.Invoke(port); } finally { Exit(); } }
        public void SetDuty(int port, int duty)
        {
            Enter();
            try { Writes.Enqueue((port, duty)); if (IgnoreCommands > 0) IgnoreCommands--; else duties[port] = duty; }
            finally { Exit(); }
        }
        public FanReading Read(int port)
        {
            Enter();
            try { ReadPorts.Enqueue(port); OnRead?.Invoke(port); if (ReadDelayMilliseconds > 0) Thread.Sleep(ReadDelayMilliseconds); return new(port, 1200, duties.GetValueOrDefault(port, 40)); }
            finally { Exit(); }
        }
        public void SetRgbFrame(int port, IReadOnlyList<LedColor> colors) { Enter(); Exit(); }
        public void PrepareToClose(bool criticalTemperature) { }
        public void Dispose() { }
    }
}
