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
        using var enteredRead = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        int iterations = 0, blockedOpens = 0; var blockedController = new FakeController();
        var blocked = new Engine(_ => { Interlocked.Increment(ref blockedOpens); return blockedController; }, () =>
        {
            var sampleTime = DateTimeOffset.UtcNow;
            if (Interlocked.Increment(ref iterations) == 2) { enteredRead.Set(); releaseRead.Wait(); }
            return new Telemetry(sampleTime, 56.125, 40, 0, 0, 30);
        }, () => []);
        try
        {
            blocked.Start(settings, true);
            Check(SpinWait.SpinUntil(() => blockedController.Writes > 0, 3000), "Fresh sample establishes control before a later blocked read");
            Check(enteredRead.Wait(3500), "Second mock sensor read reaches the blocking point");
            var inFlight = blocked.SamplerDiagnostics;
            Check(inFlight.Phase == "SensorRead" && inFlight.Probe.IterationInProgress && inFlight.Probe.IterationId == 2,
                "Atomic probe reports the current blocked sensor iteration");
            Check(inFlight.Probe.PreviousCompletedIterationId == 1 && inFlight.Probe.LastPublishedIterationId == 1,
                "Previous completed and published samples remain distinct from the in-flight iteration");
            Check(SpinWait.SpinUntil(() => blocked.ControllerFault && blockedController.Disposals == 1, 6500),
                "Blocked current read still releases control once at the unchanged stale limit");
            var fault = blocked.LastControllerFault ?? throw new InvalidOperationException("Missing blocked-read evidence");
            Check(fault.CapturedAtDecision && fault.SampleAgeSeconds > 5 && fault.Sampler.Phase == "SensorRead" &&
                fault.Sampler.Probe.IterationId == 2 && fault.Sampler.Probe.PreviousCompletedIterationId == 1,
                "Stale decision preserves the old sample and active blocked-read phase");
            Check(fault.Sampler.CurrentPhaseElapsedMilliseconds > fault.LastReadMilliseconds && fault.Sampler.LastPublishedMeasurementAgeMilliseconds >= 5000,
                "Fault exposes active read duration and measurement age instead of implying the last completed read is current");
            Check(blockedController.Writes == 1 && blockedController.CriticalCloses == 0 && blockedOpens == 1,
                "Stale telemetry sends no retry or synthetic critical command and never reconnects");
            releaseRead.Set();
            Check(SpinWait.SpinUntil(() => blocked.SamplerDiagnostics.Probe.PreviousCompletedIterationId >= 2, 3000),
                "Sampler can finish independently after the controller fault");
            Check(ReferenceEquals(fault, blocked.LastControllerFault) && fault.Sampler.Probe.PreviousCompletedIterationId == 1 &&
                blocked.SamplerDiagnostics.Probe.LastPublishedIterationId >= 2 && blockedController.Disposals == 1,
                "Later publication cannot mutate latched evidence or reopen the controller");
        }
        catch (Exception e) { File.WriteAllText(path, JsonSerializer.Serialize(new { passed = false, error = e.Message, log = blocked.Logs })); return 1; }
        finally { releaseRead.Set(); blocked.Stop().GetAwaiter().GetResult(); }
        try
        {
            // Force the publication race without timing a thread between throw
            // and catch: this is the same evidence selection used by FanLoop.
            var recorder = new SamplerDiagnosticsRecorder();
            var sampleTime = DateTimeOffset.UtcNow.AddSeconds(-6);
            long sampleTicks = Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency;
            var expired = new Telemetry(sampleTime, 56.125, 40, 0, 0, 30);
            recorder.BeginIteration(sampleTime, sampleTicks);
            recorder.BeginMeasurement(sampleTime, sampleTicks); recorder.Published(expired);
            var evidence = new ControllerFaultEvidence(DateTimeOffset.UtcNow, "Stale telemetry", expired, recorder.Snapshot(), true);
            var error = new TelemetryDecisionException(evidence);
            var fresh = new Telemetry(DateTimeOffset.UtcNow, 47, 39, 0, 0, 30);
            recorder.BeginIteration(fresh.Time, Stopwatch.GetTimestamp());
            recorder.BeginMeasurement(fresh.Time, Stopwatch.GetTimestamp()); recorder.Published(fresh);
            int fallbackReads = 0;
            var captured = ControllerFaultEvidence.FromException(error, () => { fallbackReads++; return fresh; }, recorder.Snapshot);
            Check(ReferenceEquals(captured, evidence) && fallbackReads == 0 && captured.SampleTime == expired.Time && captured.SampleAgeSeconds >= 6,
                "Fresh publication between stale decision and catch cannot replace the exact offending sample");
            Check(captured.Sampler.Probe.LastPublishedIterationId == 1 && recorder.Snapshot().Probe.LastPublishedIterationId == 2,
                "Decision-time diagnostic probe remains immutable across a fresh publication");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(captured, Settings.JsonOptions));
            var root = json.RootElement;
            Check(root.GetProperty("sampleTime").GetDateTimeOffset() == expired.Time && root.GetProperty("sampleAgeSeconds").GetDouble() >= 6 &&
                root.TryGetProperty("time", out _) && root.TryGetProperty("reason", out _) && root.TryGetProperty("lastReadMilliseconds", out _) &&
                root.TryGetProperty("sampleGapMilliseconds", out _) && root.TryGetProperty("CpuTemperature", out _) && root.TryGetProperty("GpuTemperature", out _),
                "Fault JSON preserves legacy field names and exact stale-decision values");
            Check(root.GetProperty("lastReadMeaning").GetString()!.Contains("Previous completed") && root.TryGetProperty("sampler", out _),
                "Fault JSON labels previous completed timing and includes current sampler evidence");
            var observed = recorder.Snapshot();
            var clockShift = recorder.Snapshot(observed.ObservedUtc.AddSeconds(30), observed.ObservedTimestamp);
            Check(Math.Abs((clockShift.LastPublishedClockDifferenceMilliseconds - observed.LastPublishedClockDifferenceMilliseconds)!.Value - 30000) < 1 &&
                clockShift.LastPublishedMeasurementAgeMilliseconds == observed.LastPublishedMeasurementAgeMilliseconds,
                "Clock changes are visible without changing monotonic measurement age");
        }
        catch (Exception e) { File.WriteAllText(path, JsonSerializer.Serialize(new { passed = false, error = e.Message })); return 1; }
        File.WriteAllText(path, JsonSerializer.Serialize(new { passed = true, hardwareAccess = false, count = checks.Count, checks }));
        return 0;
    }
    private sealed class FakeController : IFanController
    {
        public int Writes, Disposals, CriticalCloses;
        public void SetDuty(int port, int duty) => Interlocked.Increment(ref Writes);
        public void SetRgbFrame(int port, IReadOnlyList<LedColor> colors) { }
        public FanReading Read(int port) => new(port, 900, 40);
        public void PrepareToClose(bool criticalTemperature) { if (criticalTemperature) Interlocked.Increment(ref CriticalCloses); }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
