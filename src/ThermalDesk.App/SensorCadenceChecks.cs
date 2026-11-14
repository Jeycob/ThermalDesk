using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class SensorCadenceChecks
{
    internal static int Run(string path)
    {
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        var settings = new Settings { CoolingEnabled = true, CoolingWiringConfirmed = true, PollSeconds = 3 };
        settings.Ports[0].Role = PortRole.Fan; settings.Ports[0].Mode = ControlMode.Fixed; settings.Ports[0].FixedDuty = 40;
        var starts = new ConcurrentQueue<long>(); bool poolThread = false;
        var controller = new FakeController(); int opens = 0;
        var slow = new Engine(_ => { Interlocked.Increment(ref opens); return controller; }, () =>
        {
            poolThread |= Thread.CurrentThread.IsThreadPoolThread;
            starts.Enqueue(Stopwatch.GetTimestamp());
            var sampleTime = DateTimeOffset.UtcNow;
            Thread.Sleep(2100);
            return new Telemetry(sampleTime, 45, 40, 0, 0, 30);
        }, () => []);
        try
        {
            slow.Start(settings, true);
            Check(SpinWait.SpinUntil(() => starts.Count >= 5 || slow.ControllerFault, 14000) && !slow.ControllerFault, "Slow driver reads do not gain an extra two-second delay and falsely latch stale telemetry");
            Check(!poolThread, "Safety sampler runs on its own thread");
            var samples = starts.ToArray();
            Check(samples.Zip(samples.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b).TotalSeconds).All(gap => gap < 3.5), "Overrunning sampler stays bounded without overlapping reads");
            Check(controller.Writes > 0 && opens == 1, "Healthy slow sampling retains the same controller and configured duty");
        }
        catch (Exception e) { File.WriteAllText(path, JsonSerializer.Serialize(new { passed = false, error = e.Message, log = slow.Logs })); return 1; }
        finally { slow.Stop().GetAwaiter().GetResult(); }
        int stale = 0, stalledOpens = 0; var stalledController = new FakeController();
        var stalled = new Engine(_ => { Interlocked.Increment(ref stalledOpens); return stalledController; }, () => new Telemetry(DateTimeOffset.UtcNow.AddSeconds(Volatile.Read(ref stale) == 0 ? 0 : -10), 45, 40, 0, 0, 30), () => []);
        try
        {
            stalled.Start(settings, true);
            Check(SpinWait.SpinUntil(() => stalledController.Writes > 0, 3000), "Fresh sample establishes mock control");
            Volatile.Write(ref stale, 1);
            Check(SpinWait.SpinUntil(() => stalled.ControllerFault, 6000), "Truly stale telemetry still latches with the five-second limit unchanged");
            Check(stalled.Status.TryGetValue("Port 1", out var status) && status.Contains("Stale telemetry"), "Port status includes the shared controller fault cause");
            Check(stalled.Logs.Any(line => line.Contains("sample age") && line.Contains("last read")), "Fault log preserves sensor timing evidence");
            Thread.Sleep(300);
            Check(stalledOpens == 1 && stalledController.Disposals == 1, "Fault releases once and never automatically reconnects");
        }
        catch (Exception e) { File.WriteAllText(path, JsonSerializer.Serialize(new { passed = false, error = e.Message, log = stalled.Logs })); return 1; }
        finally { stalled.Stop().GetAwaiter().GetResult(); }
        File.WriteAllText(path, JsonSerializer.Serialize(new { passed = true, hardwareAccess = false, count = checks.Count, checks }));
        return 0;
    }
    private sealed class FakeController : IFanController
    {
        public int Writes, Disposals;
        public void SetDuty(int port, int duty) => Interlocked.Increment(ref Writes);
        public void SetRgbFrame(int port, IReadOnlyList<LedColor> colors) { }
        public FanReading Read(int port) => new(port, 900, 40);
        public void PrepareToClose(bool criticalTemperature) { }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
