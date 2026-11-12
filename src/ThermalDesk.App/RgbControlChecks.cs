using System.Collections.Concurrent;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// Only a fake controller is used. This must never open HID/SMBus or change a fan.
internal static class RgbControlChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string name) { checks++; if (!condition) throw new InvalidOperationException("RGB semantics: " + name); }
        bool Wait(Func<bool> condition) => SpinWait.SpinUntil(condition, 5000);
        var sample = new Telemetry(DateTimeOffset.UtcNow, 40, 40, 5, 5, 25);
        var legacy = new PortSettings { Port = 1 };
        Check(!Lighting.IsManaged(legacy) && Lighting.StateLabel(legacy) == "Unchanged", "old false profiles are not silently taken over");
        legacy.RgbOffRequested = true;
        var black = Lighting.OutputFrame(legacy, "FFFFFF", 0, sample);
        Check(black.Length == 12 && black.All(c => c == new LedColor()), "explicit OFF is a black frame");
        Check(legacy.Copy().RgbOffRequested && Lighting.StateLabel(legacy) == "OFF", "OFF persists across settings serialization");
        legacy.Rgb = true; legacy.RgbColor = "123456"; legacy.RgbBrightness = 100;
        Check(Lighting.OutputFrame(legacy, "FFFFFF", 0, sample).All(c => c == new LedColor(0x12, 0x34, 0x56)), "ON takes precedence for compatibility if both flags are present");
        var packet = Protocols.RgbFrame(1, [new(255, 0, 0)]);
        Check(packet.Length == 193 && packet.Skip(8).All(b => b == 0), "unused addressable LED slots are zero-padded");

        var settings = new Settings { RgbEnabled = true, StartLiveAtSignIn = true };
        settings.Ports[0].RgbOffRequested = true;
        settings.ValidateLiveSelection();
        Check(true, "explicit OFF-only configuration passes the complete live-start validator");
        Check(StartupManager.ShouldStartLive(settings, ["--startup", "--minimized"], false), "saved explicit OFF-only sign-in is a selected live device");
        var fake = new FakeController(); int opens = 0;
        var engine = new Engine(_ => { Interlocked.Increment(ref opens); return fake; }, () => sample with { Time = DateTimeOffset.UtcNow }, () => []);
        engine.Start(settings, true);
        try
        {
            Check(Wait(() => fake.Frames.Count > 0), "OFF saved before startup opens a single lighting-only controller");
            Check(fake.Frames.All(f => f.Port == 1 && f.Colors.All(c => c == new LedColor())), "only explicitly selected port is darkened");
            Check(fake.Reads == 0, "explicit OFF-only lighting does not introduce RPM polling");
            int initialCount = fake.Frames.Count;
            Check(Wait(() => fake.Frames.Count > initialCount), "explicit OFF has the same bounded keepalive as an active effect");
            var changed = settings.Copy(); changed.Ports[0].Rgb = true; changed.Ports[0].RgbOffRequested = false;
            changed.Ports[0].RgbColor = "FF0000"; changed.Ports[0].RgbBrightness = 100; changed.Ports[0].LedCount = 24;
            engine.ApplyLiveSettings(changed);
            Check(Wait(() => fake.Frames.TryPeek(out _) && fake.Frames.Last().Colors.Length == 24 && fake.Frames.Last().Colors.All(c => c.Red == 255)), "ON and LED count changes apply without session restart");
            changed.Ports[0].Rgb = false; changed.Ports[0].RgbOffRequested = true;
            engine.ApplyLiveSettings(changed);
            Check(Wait(() => fake.Frames.Last().Colors.Length == 24 && fake.Frames.Last().Colors.All(c => c == new LedColor())), "ON to OFF clears the selected LEDs");
            changed.RgbEnabled = false; engine.ApplyLiveSettings(changed);
            Check(changed.StartLiveAtSignIn && !changed.HasLiveSelection, "last-master OFF preserves the sign-in preference in an idle profile");
            Check(!StartupManager.ShouldStartLive(changed, ["--startup", "--minimized"], false), "idle armed sign-in falls back to Preview");
            Check(!StartupManager.ShouldStartLive(changed, ["--live-once"], false), "an explicit launch flag cannot start an empty hardware session");
            Thread.Sleep(1300);
            int releasedCount = fake.Frames.Count; Thread.Sleep(2200);
            Check(fake.Frames.Count == releasedCount, "global RGB disable stops continuing lighting writes");
            Check(engine.Live && engine.Running, "disabling the last output leaves an idle live session rather than restarting hardware");
            changed.RgbEnabled = true; engine.ApplyLiveSettings(changed);
            Check(Wait(() => fake.Frames.Count > releasedCount && fake.Frames.Last().Colors.All(c => c == new LedColor())), "global re-enable resumes saved OFF intent in the same session");
            Check(opens == 1 && fake.DutyWrites == 0 && !engine.ControllerFault, "all RGB edits keep the same controller and never change fan speed");
        }
        finally { Task.Run(async () => await engine.Stop()).GetAwaiter().GetResult(); }
        Check(fake.Disposals == 1 && fake.DutyWrites == 0, "lighting-only stop remains duty-free");
        var idleSettings = settings.Copy(); idleSettings.RgbEnabled = false;
        engine.Start(idleSettings, StartupManager.ShouldStartLive(idleSettings, ["--startup"], false));
        try
        {
            Check(!engine.Live && opens == 1, "idle sign-in fallback opens no hardware");
            Check(engine.Status["Session"].Contains("sign-in preference retained") && engine.Logs.Any(x => x.Contains("no live devices are selected")),
                "Preview status and log explain why live sign-in did not take over hardware");
        }
        finally { Task.Run(async () => await engine.Stop()).GetAwaiter().GetResult(); }
        return checks;
    }

    private sealed class FakeController : IFanController
    {
        internal readonly ConcurrentQueue<(int Port, LedColor[] Colors)> Frames = new();
        internal int DutyWrites, Disposals, Reads;
        public void SetDuty(int port, int duty) => Interlocked.Increment(ref DutyWrites);
        public void SetRgbFrame(int port, IReadOnlyList<LedColor> colors) => Frames.Enqueue((port, colors.ToArray()));
        public FanReading Read(int port) { Interlocked.Increment(ref Reads); return new(port, 800, 40); }
        public void PrepareToClose(bool criticalTemperature) { if (criticalTemperature) throw new InvalidOperationException("Unexpected critical simulated temperature."); }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
