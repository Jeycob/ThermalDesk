using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class WidgetChecks
{
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var t = new Telemetry(DateTimeOffset.Now, 62.5, 57, 38, 68, 41) { GpuHotSpotTemperature = 75, GpuMemoryTemperature = 70, Fans = [new(1, 1250, 60), new(2, 1400, 65)] };
        int count = 0;
        void Check(bool value, string message) { count++; if (!value) throw new InvalidOperationException(message); }
        try
        {
            foreach (var size in new[] { (480, 128), (480, 480) })
            foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                var s = new DisplaySettings { UseCustomLayout = true, Rotation = rotation };
                var canvas = WidgetLayout.Canvas(s, size.Item1, size.Item2); s.Widgets = WidgetLayout.Defaults(canvas.Width, canvas.Height);
                WidgetLayout.Validate(s, size.Item1, size.Item2);
                using var renderer = new FrameRenderer(); using var image = renderer.Render(t, s, size.Item1, size.Item2);
                Check(image.Width == size.Item1 && image.Height == size.Item2, "Rotation must preserve physical output resolution.");
                image.Save(Path.Combine(directory, $"widgets-{size.Item1}x{size.Item2}-{rotation}.png"), ImageFormat.Png);
            }
            var profile = new Settings();
            profile.Panel.UseCustomLayout = profile.Aio.UseCustomLayout = true;
            profile.Panel.Widgets = WidgetLayout.Defaults(480, 128); profile.Aio.Widgets = WidgetLayout.Defaults(480, 480);
            string path = Path.Combine(directory, "widget-profile.json"); SettingsStore.Save(path, profile); var restored = SettingsStore.Load(path);
            Check(restored.Aio.Widgets.Count == 7 && restored.Panel.Widgets.Count == 5, "Profile must round-trip both independent layouts.");
            using (var form = new WidgetDesignerForm("TOUGHLIQUID AX · 480 × 480", restored.Aio, 480, 480, () => t))
            { form.RenderForTest(Path.Combine(directory, "designer-round.png")); Check(form.Result.Widgets.Count == 7, "Designer must keep widgets."); count += form.ExerciseForTest(); Check(restored.Aio.Widgets.Count == 7, "Editing a dialog must not mutate its input profile."); }
            using (var form = new WidgetDesignerForm("3.9-inch LCD · 480 × 128", restored.Panel, 480, 128, () => t))
            { form.RenderForTest(Path.Combine(directory, "designer-wide.png")); Check(form.Result.Widgets.Count == 5, "Wide designer must keep widgets."); count += form.ExerciseForTest(); }
            using (var form = new MainForm(restored, path)) form.RenderPreview(Path.Combine(directory, "main-widgets.png"));
            using (var form = new MainForm(new Settings(), Path.Combine(directory, "automatic-settings.json"))) count += form.ExerciseAutomaticSettingsForTest();
            using (var form = new PortEditorForm(new PortSettings { Port = 1 })) count += form.ExerciseForTest(Path.Combine(directory, "port-editor.png"));
            var fake = new TestController();
            var rgbSettings = new Settings { RgbEnabled = true, MonitorFanController = true };
            rgbSettings.Ports[0].Rgb = true; rgbSettings.Ports[0].RgbEffect = RgbEffect.RainbowWave;
            var simulation = new Engine(_ => fake, () => t with { Time = DateTimeOffset.UtcNow }, () => []);
            simulation.Start(rgbSettings, true);
            Thread.Sleep(2200); Task.Run(async () => await simulation.Stop()).GetAwaiter().GetResult();
            Check(fake.DutyWrites == 0, "RGB-only session must never send fan duty writes.");
            Check(fake.LightingFrames is >= 3 and <= 14, "RGB cadence must remain bounded near five frames/second.");
            Check(simulation.Fans.Count == 5, "Read-only controller monitoring must expose all five ports.");
            int opens = 0;
            var control = new TestController();
            var cooling = new Settings { CoolingEnabled = true, CoolingWiringConfirmed = true };
            cooling.Ports[0].Role = PortRole.Fan; cooling.Ports[0].Mode = ControlMode.Curve;
            var live = new Engine(_ => { Interlocked.Increment(ref opens); return control; }, () => t with { Time = DateTimeOffset.UtcNow }, () => []);
            live.Start(cooling, true); Thread.Sleep(400);
            var changed = cooling.Copy(); FanPresets.Apply(changed.Ports[0], FanPreset.Full); live.ApplyLiveSettings(changed);
            SpinWait.SpinUntil(() => Volatile.Read(ref control.LastDuty) == 100 || live.ControllerFault, 5000);
            Check(opens == 1 && control.LastDuty == 100, $"Live Full preset applies without reopening controller (opens={opens}, duty={control.LastDuty}, fault={live.ControllerFault}, log={string.Join("; ", live.Logs)})");
            Check(control.FirstDuty is >= 30 and < 100, "Healthy initial telemetry uses the curve, not an unconditional 100% burst");
            FanPresets.Apply(changed.Ports[0], FanPreset.Silent); live.ApplyLiveSettings(changed);
            Check(SpinWait.SpinUntil(() => Volatile.Read(ref control.LastDuty) < 100 || live.ControllerFault, 7000) && !live.ControllerFault && opens == 1,
                "Silent takes effect in the existing session without Stop/Apply or reopening USB");
            var quieterDuty = control.LastDuty;
            changed.Ports[0].RgbEffect = RgbEffect.PaletteWave; changed.Ports[0].RgbPalette = ["FF0000", "AA00FF"]; changed.Ports[0].RgbDirection = RgbDirection.Reverse;
            live.ApplyLiveSettings(changed);
            Check(opens == 1 && control.LastDuty <= quieterDuty, "In-place lighting changes do not reopen USB or raise fan duty");
            bool topologyBlocked = false; changed.Ports[1].Role = PortRole.Fan; changed.Ports[1].Mode = ControlMode.Curve;
            try { live.ApplyLiveSettings(changed); } catch (InvalidOperationException) { topologyBlocked = true; }
            Check(topologyBlocked && opens == 1, "Live updates cannot silently add a managed port");
            Task.Run(async () => await live.Stop()).GetAwaiter().GetResult();
            int faultOpens = 0;
            var faulty = new Engine(_ => { Interlocked.Increment(ref faultOpens); return new FaultyController(); }, () => t with { Time = DateTimeOffset.UtcNow }, () => []);
            faulty.Start(cooling, true); Thread.Sleep(5600);
            Check(faulty.ControllerFault && faultOpens == 1 && faulty.Fans.Count == 0, "Telemetry failure latches: no repeated five-second controller resets");
            bool faultApplyBlocked = false; try { faulty.ApplyLiveSettings(cooling); } catch (InvalidOperationException) { faultApplyBlocked = true; }
            Check(faultApplyBlocked, "Fault requires explicit stop before retry");
            Task.Run(async () => await faulty.Stop()).GetAwaiter().GetResult();
            var transport = new TestFanLink();
            using (var controller = new FanController(transport, 65))
            { controller.SetDuty(1, 60); controller.SetDuty(1, 70); controller.PrepareToClose(false); }
            Check(transport.Duties.SequenceEqual(new[] { 60, 70, 40 }), "Stop restores captured duty; never injects fallback 100%");
            var emergency = new TestFanLink();
            using (var controller = new FanController(emergency, 65))
            { controller.SetDuty(1, 60); controller.PrepareToClose(true); }
            Check(emergency.Duties.SequenceEqual(new[] { 60, 100 }), "Real critical-temperature close is distinct from ordinary stop");
            var rgbOnly = new TestFanLink();
            using (var controller = new FanController(rgbOnly, 65)) controller.SetRgbFrame(1, [new(0, 255, 0)]);
            Check(rgbOnly.Duties.Count == 0, "RGB-only close never changes fan speed");
            var unavailable = new TestFanLink { FailRead = true };
            bool refused = false;
            try { using var controller = new FanController(unavailable, 65); controller.SetDuty(1, 60); } catch (IOException) { refused = true; }
            Check(refused && unavailable.Duties.Count == 0, "Unavailable baseline prevents takeover and sends no fallback 100%");
            var releaseFailure = new TestFanLink { FailRestore = true }; bool reportedReleaseFailure = false;
            try { using var controller = new FanController(releaseFailure, 65); controller.SetDuty(1, 60); }
            catch (IOException e) { reportedReleaseFailure = e.Message.Contains("Could not restore") && e.Message.Contains("1"); }
            Check(reportedReleaseFailure && !releaseFailure.Duties.Contains(100), "Failed release is reported, never hidden by a fallback 100% write");
            int invalidOpens = 0;
            var invalidStart = new Engine(_ => { invalidOpens++; return new TestController(); }, () => t with { Time = DateTimeOffset.UtcNow, CpuTemperature = null }, () => []);
            invalidStart.Start(cooling, true);
            SpinWait.SpinUntil(() => invalidStart.ControllerFault, 2000);
            Check(invalidStart.ControllerFault && invalidOpens == 0, "Unavailable initial CPU prevents hardware access instead of full-speed fallback");
            Task.Run(async () => await invalidStart.Stop()).GetAwaiter().GetResult();
            var conflict = new Engine(_ => throw new InvalidOperationException("Must not open a device"), () => t, () => ["TTRGBPlus"]);
            bool blocked = false; try { conflict.Start(rgbSettings, true); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked && !conflict.Running, "Conflicting vendor process must block before device access.");
            var startup = new Settings { Aio = new DisplaySettings { Enabled = true }, StartLiveAtSignIn = true };
            Check(StartupManager.ShouldStartLive(startup, ["--startup", "--minimized"], false), "Explicit armed sign-in should use its saved profile.");
            Check(!StartupManager.ShouldStartLive(startup, [], false), "Manual launch must remain preview even with an armed profile.");
            Check(!StartupManager.ShouldStartLive(startup, ["--startup"], true), "Shift must bypass live startup.");
            startup.StartLiveAtSignIn = false;
            Check(!StartupManager.ShouldStartLive(startup, ["--startup"], false), "Unarmed profile cannot auto-start hardware.");
            Check(StartupManager.ShouldStartLive(startup, ["--live-once"], false), "Explicit one-time live launch does not arm sign-in startup.");
            Check(!StartupManager.ShouldStartLive(startup, ["--live-once"], true), "Shift bypass also protects the explicit one-time launcher.");
            bool emptyBlocked = false;
            try { new Engine(_ => throw new Exception("No device should open"), () => t, () => []).Start(new Settings(), true); }
            catch (ArgumentException) { emptyBlocked = true; }
            Check(emptyBlocked, "Empty live selection must be rejected before opening a device.");
            using var process = Process.GetCurrentProcess();
            using var repeated = new FrameRenderer();
            using (var warmup = repeated.Render(t, restored.Aio, 480, 480)) { }
            int before = GetGuiResources(process.Handle, 0);
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) { using var image = repeated.Render(t with { Time = t.Time.AddSeconds(i) }, restored.Aio, 480, 480); }
            clock.Stop(); int after = GetGuiResources(process.Handle, 0);
            Check(after - before <= 2, "GDI resource count grew during 200 repeated widget renders.");
            File.WriteAllText(Path.Combine(directory, "widget-checks.json"), JsonSerializer.Serialize(new { passed = true, checks = count, hardwareAccess = false, renders = 200, elapsedMilliseconds = clock.Elapsed.TotalMilliseconds, gdiBefore = before, gdiAfter = after }, Settings.JsonOptions));
            return 0;
        }
        catch (Exception e)
        { File.WriteAllText(Path.Combine(directory, "widget-checks.json"), JsonSerializer.Serialize(new { passed = false, checks = count, hardwareAccess = false, error = e.ToString() }, Settings.JsonOptions)); return 1; }
    }
    private sealed class TestController : IFanController
    {
        public int DutyWrites, LightingFrames, LastDuty, FirstDuty = -1;
        public void SetDuty(int port, int duty) { Interlocked.Increment(ref DutyWrites); Interlocked.CompareExchange(ref FirstDuty, duty, -1); LastDuty = duty; }
        public void SetRgbFrame(int port, IReadOnlyList<LedColor> colors) => Interlocked.Increment(ref LightingFrames);
        public FanReading Read(int port) => new(port, 1200 + port * 100, 40);
        public void PrepareToClose(bool criticalTemperature) { }
        public void Dispose() { }
    }
    private sealed class FaultyController : IFanController
    {
        public void SetDuty(int port, int duty) { }
        public void SetRgbFrame(int port, IReadOnlyList<LedColor> colors) { }
        public FanReading Read(int port) => throw new IOException("Injected telemetry failure");
        public void PrepareToClose(bool criticalTemperature) { }
        public void Dispose() { }
    }
    private sealed class TestFanLink : IFanLink
    {
        private readonly Queue<byte[]> replies = new();
        private int duty = 40;
        public List<int> Duties { get; } = [];
        public bool FailRead;
        public bool FailRestore;
        public void Write(byte[] packet)
        {
            if (packet[1] == 0x33 && packet[2] == 0x51) replies.Enqueue([0, 0x33, 0x51, packet[3], 0, (byte)duty, 0xe8, 3]);
            else
            {
                if (packet[1] == 0x32 && packet[2] == 0x51)
                { if (FailRestore && packet[5] == 40) throw new IOException("Injected restore failure"); duty = packet[5]; Duties.Add(duty); }
                replies.Enqueue([0, packet[1], packet[2], packet[3], 0, 0, 0, 0]);
            }
        }
        public byte[] Read(int length, int timeoutMilliseconds) => FailRead ? throw new IOException("USB unavailable") : replies.Dequeue();
        public void Dispose() { }
    }
}
