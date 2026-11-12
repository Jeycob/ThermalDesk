using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class WidgetChecks
{
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    public static int BenchmarkDisplayLoops(string output)
    {
        // Fully injected transports/sensors: never opens HID, LHM, saved profiles or a UI.
        var settings = new Settings(); settings.Panel.Enabled = settings.Aio.Enabled = true;
        settings.Panel.FramesPerSecond = settings.Aio.FramesPerSecond = 10;
        settings.Panel.UseCustomLayout = settings.Aio.UseCustomLayout = true;
        settings.Panel.Widgets = WidgetLayout.Defaults(480, 128); settings.Aio.Widgets = WidgetLayout.Defaults(480, 480);
        var panel = new TestLcd(); var aio = new TestLcd();
        var engine = new Engine(_ => throw new InvalidOperationException("No fan controller permitted in benchmark"), () => new(DateTimeOffset.UtcNow, 46, 42, 10, 20, 35), () => [], displayFactory: (isPanel, _) => isPanel ? panel : aio);
        var samples = new List<object>(); using var process = Process.GetCurrentProcess(); var clock = Stopwatch.StartNew();
        TimeSpan baseline = TimeSpan.Zero; double baselineTime = 0; long allocated = 0;
        try
        {
            engine.Start(settings, true);
            for (int second = 0; second <= 20; second++)
            {
                if (second == 5) { baseline = process.TotalProcessorTime; baselineTime = clock.Elapsed.TotalSeconds; allocated = GC.GetTotalAllocatedBytes(false); }
                if (second >= 5 && second % 5 == 0)
                {
                    process.Refresh(); samples.Add(new { seconds = second, workingSetMiB = process.WorkingSet64 / 1048576d, threads = process.Threads.Count, handles = process.HandleCount, gdiObjects = GetGuiResources(process.Handle, 0), userObjects = GetGuiResources(process.Handle, 1), panelFrames = Volatile.Read(ref panel.Frames), aioFrames = Volatile.Read(ref aio.Frames) });
                }
                if (second < 20) Thread.Sleep(1000);
            }
            double cpuPercent = (process.TotalProcessorTime - baseline).TotalSeconds / (clock.Elapsed.TotalSeconds - baselineTime) / Environment.ProcessorCount * 100;
            Task.Run(async () => await engine.Stop()).GetAwaiter().GetResult();
            bool passed = panel.Frames >= 100 && aio.Frames >= 100 && panel.Disposals == 1 && aio.Disposals == 1;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed, hardwareAccess = false, workload = "Two LCD loops at up to 10 FPS each, with unchanged-frame JPEG reuse; mock transport additionally decodes/hashes JPEGs. No USB, sensor driver or real hardware timing.", logicalProcessors = Environment.ProcessorCount, seconds = clock.Elapsed.TotalSeconds, averageTotalCpuPercent = cpuPercent, allocatedMiBAfterWarmup = (GC.GetTotalAllocatedBytes(false) - allocated) / 1048576d, renders = engine.FrameRenderCounts, panelFrames = panel.Frames, aioFrames = aio.Frames, samples }, Settings.JsonOptions));
            return passed ? 0 : 1;
        }
        finally { Task.Run(async () => await engine.Stop()).GetAwaiter().GetResult(); }
    }
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var t = new Telemetry(DateTimeOffset.Now, 62.5, 57, 38, 68, 41) { GpuHotSpotTemperature = 75, GpuMemoryTemperature = 70, Fans = [new(1, 1250, 60), new(2, 1400, 65)] };
        int count = 0;
        void Check(bool value, string message) { count++; if (!value) throw new InvalidOperationException(message); }
        try
        {
            count += RgbControlChecks.Run();
            count += NewWidgetChecks.Run(directory);
            count += LogAndPerformanceChecks.Run(directory);
            count += PowerGraphChecks.Run(directory);
            foreach (var shape in new[] { (Width: 480, Height: 128), (Width: 480, Height: 480) })
            {
                using var editor = new DisplayEditor("Offline preset checks", new DisplaySettings { Brightness = 43, FramesPerSecond = 3 }, shape.Width, shape.Height, () => t);
                count += editor.ExercisePresetsForTest(shape.Width, shape.Height);
                foreach (var preset in LcdPresets.Available(shape.Width, shape.Height))
                {
                    var s = LcdPresets.Create(new(), preset.Id, shape.Width, shape.Height);
                    using var renderer = new FrameRenderer();
                    using var image = renderer.Render(t, s, shape.Width, shape.Height);
                    Check(image.Width == shape.Width && image.Height == shape.Height, "Preset keeps the physical USB frame size: " + preset.Id);
                    using var wire = new MemoryStream(FrameRenderer.Jpeg(image)); using var decoded = new Bitmap(wire);
                    Check(decoded.Width == shape.Width && decoded.Height == shape.Height, "Preset JPEG keeps native resolution: " + preset.Id);
                    image.Save(Path.Combine(directory, "preset-" + preset.Id + "-physical.png"), ImageFormat.Png);
                    // Upright installation preview: portrait panels are physically mounted sideways.
                    if (preset.Portrait) image.RotateFlip(RotateFlipType.Rotate270FlipNone);
                    image.Save(Path.Combine(directory, "preset-" + preset.Id + "-upright.png"), ImageFormat.Png);
                    foreach (int angle in shape.Width == shape.Height ? new[] { 0, 90, 180, 270 } : preset.Portrait ? new[] { 90, 270 } : new[] { 0, 180 })
                    {
                        s.Rotation = angle; WidgetLayout.Validate(s, shape.Width, shape.Height);
                        using var rotated = renderer.Render(t, s, shape.Width, shape.Height);
                        Check(rotated.Width == shape.Width && rotated.Height == shape.Height, "Inverted mounting retains native resolution: " + preset.Id);
                    }
                    var logical = WidgetLayout.Canvas(s, shape.Width, shape.Height);
                    using var measureImage = new Bitmap(logical.Width, logical.Height); using var graphics = Graphics.FromImage(measureImage);
                    foreach (var widget in s.Widgets.Where(w => w.Kind == WidgetKind.Text || w.GaugeShowText))
                    {
                        using var font = new Font(widget.FontFamily, widget.FontSize, widget.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                        var value = WidgetLayout.Format(widget, t with { CpuTemperature = 100, GpuTemperature = 100, CpuLoad = 100, GpuLoad = 100, MemoryUsedPercent = 100 });
                        var measured = LogWidget.IsLog(widget.Source) && widget.LogAutoFit ? renderer.MeasureLogText(graphics, widget, value) : graphics.MeasureString(value, font);
                        Check(measured.Width <= widget.Width, "Preset text must not ellipsize at 100: " + preset.Id + "/" + widget.Name);
                        float textHeight = LogWidget.IsLog(widget.Source) ? measured.Height : font.Size;
                        Check(textHeight <= widget.Height, $"Preset font must fit its height: {preset.Id}/{widget.Name} ({textHeight:0.0} > {widget.Height})");
                    }
                }
            }
            Check(FrameRenderer.FormatTemperature(100) == "100 °C", "Built-in LCD dashboard shows explicit Celsius units");
            Check(FrameRenderer.FormatTemperature(null) == "—" && FrameRenderer.FormatTemperature(double.NaN) == "—", "Built-in dashboard never formats missing or non-finite temperatures as valid data");
            foreach (var size in new[] { (480, 128), (480, 480) })
            {
                using var renderer = new FrameRenderer();
                using var image = renderer.Render(t with { CpuTemperature = 100, GpuTemperature = 115 }, new DisplaySettings(), size.Item1, size.Item2);
                image.Save(Path.Combine(directory, $"dashboard-units-{size.Item1}x{size.Item2}.png"), ImageFormat.Png);
            }
            foreach (var sample in new (WidgetSource Source, string Text, string Format)[]
            {
                (WidgetSource.CpuTemperature, "CPU {value} °C", "0.0"), (WidgetSource.CpuMaximumTemperature, "CPU MAX {value} °C", "0.0"),
                (WidgetSource.GpuTemperature, "GPU {value} °C", "0.0"), (WidgetSource.GpuHotSpotTemperature, "GPU HOTSPOT {value} °C", "0.0"),
                (WidgetSource.GpuMemoryTemperature, "GPU MEMORY {value} °C", "0.0"), (WidgetSource.CpuLoad, "CPU {value}%", "0"),
                (WidgetSource.GpuLoad, "GPU {value}%", "0"), (WidgetSource.MemoryUsedPercent, "RAM {value}%", "0"),
                (WidgetSource.FanRpm, "FAN {value} RPM", "0"), (WidgetSource.FanDuty, "FAN {value}%", "0"),
                (WidgetSource.Clock, "{value}", "HH:mm:ss"), (WidgetSource.Date, "{value}", "yyyy-MM-dd")
            })
            {
                var widget = new LcdWidget { TextTemplate = "Previous unit must not survive a source change" }; var adapter = new WidgetProperties(widget); adapter.Source = sample.Source;
                Check(widget.TextTemplate == sample.Text, "Source change supplies the correct default label/unit: " + sample.Source);
                Check(widget.ValueFormat == sample.Format, "Source change supplies the correct numeric/time format: " + sample.Source);
            }
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
            using (var form = new MainForm(new Settings(), Path.Combine(directory, "profile-import-settings.json"))) count += form.ExerciseProfileImportForTest(directory);
            using (var form = new MainForm(new Settings(), Path.Combine(directory, "window-settings.json"))) count += form.ExerciseWindowPreferencesForTest(Path.Combine(directory, "window-preferences.png"));
            Check(ProcessActivityMonitor.TryParseInstance("pid_1234_luid_0x00000000_0x00012345_phys_0_eng_0_engtype_Compute", out int parsedPid, out string parsedEngine) && parsedPid == 1234 && parsedEngine == "Compute", "GPU counter PID/engine parser");
            Check(!ProcessActivityMonitor.TryParseInstance("pid_bad_luid_x_engtype_3D", out _, out _) && !ProcessActivityMonitor.TryParseInstance("unrelated", out _, out _), "malformed GPU instances ignored");
            using (var form = new MainForm(new Settings(), Path.Combine(directory, "ui-lifecycle-settings.json"))) count += form.ExerciseUiLifecycleForTest(Path.Combine(directory, "display-settings-minimum.png"));
            using (var form = new PortEditorForm(new PortSettings { Port = 1 })) count += form.ExerciseForTest(Path.Combine(directory, "port-editor.png"));
            var sourceTelemetry = t with { CpuTemperatureSource = "Core (Tctl/Tdie)", CpuMaximumTemperatureSource = "CPU CCD1 (Tdie)", GpuDeviceId = "rtx", GpuDeviceName = "NVIDIA RTX 4080", GpuTemperatureSource = "GPU Core", MemoryLoadSource = "Memory", GpuDevices = [new("rtx", "NVIDIA RTX 4080"), new("igpu", "Integrated AMD")] };
            using (var sourceForm = new Form { ClientSize = new Size(900, 500), Font = new Font("Segoe UI", 10), StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false })
            {
                var selector = new SensorSelector("missing-saved-gpu", () => sourceTelemetry); sourceForm.Controls.Add(selector);
                count += selector.ExerciseForTest(); sourceForm.Show(); sourceForm.PerformLayout(); Application.DoEvents();
                using var image = new Bitmap(sourceForm.Width, sourceForm.Height); sourceForm.DrawToBitmap(image, new Rectangle(Point.Empty, sourceForm.Size)); image.Save(Path.Combine(directory, "sensor-selector.png"), ImageFormat.Png);
            }
            var allFans = new Settings { CoolingEnabled = true, CoolingWiringConfirmed = true, PollSeconds = 10 };
            foreach (var port in allFans.Ports) { port.Role = PortRole.Fan; port.Mode = ControlMode.Fixed; port.FixedDuty = 65; port.MinimumDuty = 30; port.Curve = [new(20, 45), new(100, 45)]; }
            Check(Engine.SensorInterval(allFans) == 2, "Active cooling keeps telemetry fresh even when general polling is ten seconds");
            Check(Engine.SensorInterval(new Settings { PollSeconds = 10 }) == 10, "Monitoring-only polling remains operator-configured");
            int uiOpens = 0; var uiController = new TestController();
            var uiEngine = new Engine(_ => { Interlocked.Increment(ref uiOpens); return uiController; }, () => t with { Time = DateTimeOffset.UtcNow, CpuTemperature = 50, GpuTemperature = 40 }, () => []);
            uiEngine.Start(allFans, true);
            try { using var form = new MainForm(allFans, Path.Combine(directory, "automatic-live-profile.json"), uiEngine); count += form.ExerciseLiveAutomaticSettingsForTest(Path.Combine(directory, "cooling-feedback.png")); Check(uiOpens == 1, "All-five-fan UI changes use exactly one mock controller connection"); }
            finally { Task.Run(async () => await uiEngine.Stop()).GetAwaiter().GetResult(); }
            Check(uiEngine.Fans.Count == 0 && uiEngine.FanCommands.Count == 0, "Stop clears stale fan feedback and command displays");
            var fake = new TestController();
            var rgbSettings = new Settings { RgbEnabled = true, MonitorFanController = true };
            rgbSettings.Ports[0].Rgb = true; rgbSettings.Ports[0].RgbEffect = RgbEffect.RainbowWave;
            var simulation = new Engine(_ => fake, () => t with { Time = DateTimeOffset.UtcNow }, () => []);
            simulation.Start(rgbSettings, true);
            Thread.Sleep(2200); Check(simulation.Fans.Count == 5, "Read-only controller monitoring must expose all five ports.");
            Task.Run(async () => await simulation.Stop()).GetAwaiter().GetResult();
            Check(fake.DutyWrites == 0, "RGB-only session must never send fan duty writes.");
            Check(fake.LightingFrames is >= 3 and <= 14, "RGB cadence must remain bounded near five frames/second.");
            Check(simulation.Fans.Count == 0, "Stopped controller monitoring must not display old RPM as current.");
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
            var invalidStart = new Engine(_ => { invalidOpens++; return new TestController(); }, () => t with { Time = DateTimeOffset.UtcNow, CpuTemperature = null }, () => [], TimeSpan.FromMilliseconds(250));
            invalidStart.Start(cooling, true);
            SpinWait.SpinUntil(() => invalidStart.ControllerFault, 2000);
            Check(invalidStart.ControllerFault && invalidOpens == 0, "Unavailable initial CPU prevents hardware access instead of full-speed fallback");
            Check(invalidStart.Status["Controller"].Contains("before fan USB access"), "Readiness timeout reports no fan ownership rather than claiming a release");
            Task.Run(async () => await invalidStart.Stop()).GetAwaiter().GetResult();
            int delayedSamples = 0, delayedOpens = 0; var delayedController = new TestController();
            var delayed = new Engine(_ => { Interlocked.Increment(ref delayedOpens); return delayedController; }, () =>
            {
                int sample = Interlocked.Increment(ref delayedSamples);
                return t with { Time = DateTimeOffset.UtcNow, CpuTemperature = sample == 1 ? null : 50, GpuTemperature = 40 };
            }, () => [], TimeSpan.FromSeconds(5));
            delayed.Start(cooling, true);
            Check(SpinWait.SpinUntil(() => Volatile.Read(ref delayedSamples) >= 1 && delayed.Status.TryGetValue("Controller", out var state) && state.StartsWith("Waiting"), 1500) && delayedOpens == 0,
                "An initial timestamp without temperatures does not open the controller");
            var pendingSilent = cooling.Copy(); pendingSilent.Ports[0].MinimumDuty = 30; FanPresets.Apply(pendingSilent.Ports[0], FanPreset.Silent); delayed.ApplyLiveSettings(pendingSilent);
            Check(SpinWait.SpinUntil(() => Volatile.Read(ref delayedOpens) == 1 && delayedController.FirstDuty == 30 || delayed.ControllerFault, 6000) && !delayed.ControllerFault && delayedController.FirstDuty == 30,
                "Driver readiness uses the latest pending Silent profile without an initial full-speed burst");
            Check(delayedOpens == 1 && delayedSamples >= 2, "Readiness wait does not reconnect or repeatedly initialize USB");
            Task.Run(async () => await delayed.Stop()).GetAwaiter().GetResult();
            int cancelledOpens = 0;
            var cancelledStartup = new Engine(_ => { Interlocked.Increment(ref cancelledOpens); return new TestController(); }, () => t with { Time = DateTimeOffset.UtcNow, CpuTemperature = null }, () => [], TimeSpan.FromSeconds(5));
            cancelledStartup.Start(cooling, true);
            Check(SpinWait.SpinUntil(() => cancelledStartup.Status.TryGetValue("Controller", out var state) && state.StartsWith("Waiting"), 1500), "Readiness wait is observable before cancellation");
            Task.Run(async () => await cancelledStartup.Stop()).GetAwaiter().GetResult();
            Check(cancelledOpens == 0 && !cancelledStartup.ControllerFault && !cancelledStartup.Running, "Stop during readiness opens no device and does not invent a fault or speed write");
            int hotOpens = 0; var hotController = new TestController();
            var hotStartup = new Engine(_ => { Interlocked.Increment(ref hotOpens); return hotController; }, () => t with { Time = DateTimeOffset.UtcNow, CpuTemperature = null, GpuTemperature = 90 }, () => [], TimeSpan.FromSeconds(2));
            hotStartup.Start(cooling, true);
            Check(SpinWait.SpinUntil(() => hotController.FirstDuty == 100, 1500) && hotOpens == 1, "Genuine fresh critical GPU heat is not confused with a missing-sensor fallback");
            Task.Run(async () => await hotStartup.Stop()).GetAwaiter().GetResult();
            var conflict = new Engine(_ => throw new InvalidOperationException("Must not open a device"), () => t, () => ["TTRGBPlus"]);
            bool blocked = false; try { conflict.Start(rgbSettings, true); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked && !conflict.Running, "Conflicting vendor process must block before device access.");
            var startup = new Settings { Aio = new DisplaySettings { Enabled = true }, StartLiveAtSignIn = true };
            Check(StartupManager.ShouldRequestElevation([], false), "Normal interactive launch requests administrator consent");
            Check(StartupManager.ShouldRequestElevation(["--minimized"], false), "Limited sign-in preview cannot silently stay unelevated");
            Check(StartupManager.ShouldRequestElevation(["--startup", "--minimized"], false), "Limited startup requests elevation without changing its explicit arguments");
            Check(!StartupManager.ShouldRequestElevation([], true), "Elevated sign-in task does not request another UAC launch");
            Check(new[] { "--widget-check", "--benchmark-display-loops", "--render-preview", "--benchmark-preview" }.All(command => !StartupManager.ShouldRequestElevation([command, "output"], false)), "Exact software-only check commands stay unprivileged");
            Check(StartupManager.ShouldRequestElevation(["--widget-check"], false), "Incomplete offline command cannot bypass normal elevation policy");
            Check(StartupManager.ShouldRequestElevation(["--render-preview", "output", "--startup"], false), "Mixed malformed arguments are not whitelisted as offline mode");
            var elevation = StartupManager.ElevationStartInfo(@"C:\Program Files\ThermalDesk\ThermalDesk.exe", ["--preview", "--status-output", @"C:\A directory\status.json"]);
            Check(elevation.UseShellExecute && elevation.Verb == "runas" && elevation.FileName == @"C:\Program Files\ThermalDesk\ThermalDesk.exe" && elevation.WorkingDirectory == @"C:\Program Files\ThermalDesk", "Elevation uses Windows consent and exact executable/working directory");
            Check(elevation.ArgumentList.SequenceEqual(new[] { "--preview", "--status-output", @"C:\A directory\status.json" }), "Elevation preserves arguments without inserting hidden live flags");
            Check(!StartupManager.ShouldStartLive(startup, elevation.ArgumentList.ToArray(), false), "Elevated manual preview stays preview even with armed saved live startup");
            Check(StartupManager.ShouldStartLive(startup, ["--startup", "--minimized"], false), "Explicit armed sign-in should use its saved profile.");
            Check(StartupManager.ShouldStartLive(startup, [], false), "Manual launch resumes a previously armed saved profile.");
            Check(!StartupManager.ShouldStartLive(startup, ["--preview"], false), "Explicit Preview bypasses an armed saved profile.");
            Check(!StartupManager.ShouldStartLive(startup, ["--preview", "--live-once", "--startup"], false), "Explicit Preview wins over all automatic/live flags.");
            Check(!StartupManager.ShouldStartLive(startup, ["--minimized"], false), "Legacy Preview Run entry cannot become Live when manual resume is enabled.");
            Check(!StartupManager.ShouldStartLive(startup, ["--startup", "--preview", "--minimized"], false), "Explicit Preview sign-in always remains Preview.");
            startup.ResumeLastProfileOnLaunch = false;
            Check(!StartupManager.ShouldStartLive(startup, [], false), "Explicit resume opt-out keeps manual launch in Preview.");
            Check(StartupManager.ShouldStartLive(startup, ["--startup"], false), "Manual resume preference does not alter separately armed sign-in startup.");
            startup.ResumeLastProfileOnLaunch = true; startup.StartLiveAtSignIn = false;
            Check(StartupManager.ShouldStartLive(startup, [], false), "Explicit manual resume does not require a Windows sign-in task.");
            Check(!StartupManager.ShouldStartLive(startup, ["--startup"], false), "Manual resume never arms Windows sign-in Live by itself.");
            Check(!StartupManager.ShouldStartLive(startup, [], true), "Shift bypasses saved manual resume.");
            startup.ResumeLastProfileOnLaunch = null; startup.StartLiveAtSignIn = true;
            Check(!StartupManager.ShouldStartLive(startup, ["--startup"], true), "Shift must bypass live startup.");
            startup.StartLiveAtSignIn = false;
            Check(!StartupManager.ShouldStartLive(startup, ["--startup"], false), "Unarmed profile cannot auto-start hardware.");
            Check(StartupManager.ShouldStartLive(startup, ["--live-once"], false), "Explicit one-time live launch does not arm sign-in startup.");
            Check(!StartupManager.ShouldStartLive(startup, ["--live-once"], true), "Shift bypass also protects the explicit one-time launcher.");
            Check(!StartupManager.ShouldStartLive(startup, ["--live-once"], false, true), "Recovered profile cannot be armed by a one-time startup flag");
            startup.StartLiveAtSignIn = true;
            Check(!StartupManager.ShouldStartLive(startup, ["--startup"], false, true), "Recovered profile cannot be armed by a scheduled sign-in launch");
            startup.ResumeLastProfileOnLaunch = true;
            Check(!StartupManager.ShouldStartLive(startup, [], false, true), "Recovered profile cannot auto-resume even with an explicit saved preference");
            startup.Aio.Enabled = false; startup.Panel.Enabled = false; startup.AllowMonitoringOnly = false;
            Check(!StartupManager.ShouldStartLive(startup, [], false), "No selected live output means Preview despite saved resume preference");
            Check(StartupManager.ShouldStartMinimized(new Settings(), ["--minimized"], false) && !StartupManager.ShouldStartMinimized(new Settings(), ["--minimized"], true), "Recovery warning cannot be hidden by a minimized sign-in flag");
            var windowPreferences = new Settings { MinimizeAfterStartup = true, MinimizeWhenClosed = true };
            Check(StartupManager.ShouldStartMinimized(windowPreferences, [], false), "saved minimize preference works for a normal launch");
            Check(!StartupManager.ShouldStartMinimized(windowPreferences, [], true), "recovery stays visible with saved minimize enabled");
            Check(!StartupManager.ShouldStartMinimized(windowPreferences, [], false, true), "Shift overrides minimize for troubleshooting");
            Check(!StartupManager.ShouldStartMinimized(windowPreferences, ["--show-window"], false), "explicit show-window overrides saved minimize");
            windowPreferences.MinimizeAfterStartup = false;
            Check(!StartupManager.ShouldStartMinimized(windowPreferences, ["--startup", "--minimized"], false), "unchecked preference overrides a legacy minimized startup task");
            Check(!StartupManager.ShouldStartLive(windowPreferences, [], false), "window preferences never arm live hardware");
            Check(StartupManager.ShouldHideOnClose(true, CloseReason.UserClosing, false), "X / Alt-F4 hides only when enabled");
            Check(!StartupManager.ShouldHideOnClose(false, CloseReason.UserClosing, false), "unchecked close preference still exits");
            Check(!StartupManager.ShouldHideOnClose(true, CloseReason.UserClosing, true), "explicit tray Exit always bypasses hiding");
            Check(new[] { CloseReason.WindowsShutDown, CloseReason.TaskManagerClosing, CloseReason.ApplicationExitCall, CloseReason.FormOwnerClosing }.All(reason => !StartupManager.ShouldHideOnClose(true, reason, false)), "shutdown and external close reasons are not trapped in the tray");
            string recoveredPath = Path.Combine(directory, "recovery-ui-profile.json");
            File.WriteAllText(recoveredPath, "null"); var recovered = SettingsStore.LoadForStartup(recoveredPath);
            using (var recoveryForm = new MainForm(recovered.Settings, recoveredPath, startupWarning: recovered.Warning)) count += recoveryForm.VerifyRecoveryUiForTest(Path.Combine(directory, "recovery-ui.png"));
            bool emptyBlocked = false;
            try { new Engine(_ => throw new Exception("No device should open"), () => t, () => []).Start(new Settings(), true); }
            catch (ArgumentException) { emptyBlocked = true; }
            Check(emptyBlocked, "Empty live selection must be rejected before opening a device.");
            var blockedSettings = new Settings { MonitorFanController = true, ResumeLastProfileOnLaunch = true };
            var blockedEngine = new Engine(_ => throw new Exception("No device may open during conflict fallback"), () => t, () => ["TTRGBPlus"]);
            try
            {
                using var blockedForm = new MainForm(blockedSettings, Path.Combine(directory, "blocked-startup.json"), blockedEngine) { StartupRequested = true };
                string? reason = blockedForm.StartInitialSession();
                Check(reason?.Contains("conflicting") == true && blockedEngine.Running && !blockedEngine.Live, "Conflicting automatic startup falls back to hardware-free Preview");
            }
            finally { Task.Run(async () => await blockedEngine.Stop()).GetAwaiter().GetResult(); }
            count += CheckDisplayWorkers(t);
            count += CheckDynamicLiveSettings(t);
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
    private static int CheckDynamicLiveSettings(Telemetry telemetry)
    {
        int checks = 0;
        void Verify(bool value, string name) { checks++; if (!value) throw new InvalidOperationException("Dynamic live settings: " + name); }
        bool Wait(Func<bool> value) => SpinWait.SpinUntil(value, 5000);
        var settings = new Settings { CoolingEnabled = true, CoolingWiringConfirmed = true };
        settings.Ports[0].Role = PortRole.Fan; settings.Ports[0].Mode = ControlMode.Fixed; settings.Ports[0].FixedDuty = 40;
        var controller = new TestController(); int opens = 0, panelOpens = 0, aioOpens = 0;
        var panels = new System.Collections.Concurrent.ConcurrentQueue<TestLcd>();
        var aios = new System.Collections.Concurrent.ConcurrentQueue<TestLcd>();
        var brightnesses = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var engine = new Engine(_ => { Interlocked.Increment(ref opens); return controller; }, () => telemetry with { Time = DateTimeOffset.UtcNow, CpuTemperature = 40, GpuTemperature = 40 }, () => [], displayFactory: (panel, brightness) =>
        {
            var lcd = new TestLcd(); brightnesses.Enqueue(brightness);
            if (panel) { panels.Enqueue(lcd); Interlocked.Increment(ref panelOpens); }
            else { aios.Enqueue(lcd); Interlocked.Increment(ref aioOpens); }
            return lcd;
        });
        engine.Start(settings, true);
        try
        {
            Verify(Wait(() => controller.DutyWrites > 0), "initial mock cooling starts");
            Verify(panelOpens == 0 && aioOpens == 0, "disabled LCD workers never open USB");
            var changed = settings.Copy(); changed.Panel.Enabled = changed.Aio.Enabled = true;
            changed.Panel.Brightness = 41; changed.Aio.Brightness = 61;
            changed.Panel.FramesPerSecond = changed.Aio.FramesPerSecond = 10;
            changed.RgbEnabled = changed.MonitorFanController = true; changed.Ports[0].Rgb = true; changed.Ports[0].RgbColor = "112233";
            changed.PollSeconds = 3; engine.ApplyLiveSettings(changed);
            Verify(Wait(() => panels.Any(p => p.Frames > 0) && aios.Any(p => p.Frames > 0) && controller.LightingFrames > 0), "LCDs and RGB enable in the existing session");
            Verify(opens == 1 && controller.LastDuty == 40 && !engine.ControllerFault, "safe edits never reopen or accelerate fans");
            var firstPanel = panels.First(); var firstAio = aios.First(); changed.Panel.Brightness = 42;
            engine.ApplyLiveSettings(changed);
            Verify(Wait(() => panelOpens == 2 && panels.Last().Frames > 0), "brightness reopens only the affected LCD");
            Verify(firstPanel.Disposals == 1 && firstAio.Disposals == 0 && aioOpens == 1 && opens == 1 && brightnesses.Contains(42), "brightness uses the new value, preserves other LCD and cooling");
            changed.Panel.Enabled = false; changed.RgbEnabled = false; changed.MonitorFanController = false;
            engine.ApplyLiveSettings(changed);
            Verify(Wait(() => panels.Last().Disposals == 1 && controller.BlackFrames > 0), "disabled LCD releases once and RGB receives one black frame");
            int disabledFrames = panels.Sum(p => p.Frames), blackFrames = controller.BlackFrames;
            Thread.Sleep(350);
            Verify(panels.Sum(p => p.Frames) == disabledFrames && controller.BlackFrames == blackFrames && firstAio.Frames > 0 && opens == 1 && controller.LastDuty == 40,
                "disabled channels stay idle without shared-controller reset or repeated black writes");
            changed.Panel.Enabled = true; engine.ApplyLiveSettings(changed);
            Verify(Wait(() => panelOpens == 3 && panels.Last().Frames > 0), "LCD re-enables without restarting session");
            Verify(opens == 1 && controller.FirstDuty == 40 && controller.LastDuty == 40, "all dynamic operations preserve cooling at forty percent");
        }
        finally { Task.Run(async () => await engine.Stop()).GetAwaiter().GetResult(); }
        Verify(panels.All(p => p.Disposals == 1) && aios.All(p => p.Disposals == 1), "every enabled/disabled LCD instance closes exactly once");

        var displayOnly = new Settings(); displayOnly.Aio.Enabled = true; displayOnly.Aio.FramesPerSecond = 10;
        int dormantOpens = 0; var rgbOnly = new TestController(); var display = new TestLcd();
        var dormant = new Engine(_ => { Interlocked.Increment(ref dormantOpens); return rgbOnly; }, () => telemetry with { Time = DateTimeOffset.UtcNow }, () => [], displayFactory: (_, _) => display);
        dormant.Start(displayOnly, true);
        try
        {
            Verify(Wait(() => display.Frames > 0) && dormantOpens == 0, "LCD-only session leaves dormant controller worker unopened");
            var enabled = displayOnly.Copy(); enabled.RgbEnabled = true; enabled.MonitorFanController = true; enabled.Ports[0].Rgb = true;
            dormant.ApplyLiveSettings(enabled);
            Verify(Wait(() => dormantOpens == 1 && rgbOnly.LightingFrames > 0 && dormant.Fans.Count == 5), "RGB/monitor enable starts one controller from a display-only session");
            Verify(rgbOnly.DutyWrites == 0 && !dormant.ControllerFault, "RGB-only activation cannot take over any Preserve fan port");
            enabled.RgbEnabled = enabled.MonitorFanController = false; dormant.ApplyLiveSettings(enabled);
            Verify(Wait(() => rgbOnly.BlackFrames == 1 && dormant.Fans.Count == 0), "RGB/monitor disable sends one black frame and clears obsolete readings");
            int frames = rgbOnly.LightingFrames; Thread.Sleep(350);
            Verify(rgbOnly.LightingFrames == frames && rgbOnly.DutyWrites == 0 && dormantOpens == 1 && display.Disposals == 0,
                "disabled RGB sends no continuing writes and does not reconnect controller or LCD");
        }
        finally { Task.Run(async () => await dormant.Stop()).GetAwaiter().GetResult(); }
        return checks;
    }
    private static int CheckDisplayWorkers(Telemetry telemetry)
    {
        int count = 0;
        void Check(bool condition, string name) { count++; if (!condition) throw new InvalidOperationException("LCD worker: " + name); }
        bool Wait(Func<bool> condition) => SpinWait.SpinUntil(condition, 5000);
        var settings = new Settings();
        settings.Panel.Enabled = settings.Aio.Enabled = true;
        settings.Panel.FramesPerSecond = settings.Aio.FramesPerSecond = 10;
        settings.Panel.UseCustomLayout = settings.Aio.UseCustomLayout = true;
        settings.Panel.Brightness = 43; settings.Aio.Brightness = 67;
        settings.Panel.Widgets = [new() { Source = WidgetSource.Text, TextTemplate = "PANEL BEFORE", X = 8, Y = 8, Width = 460, Height = 100 }];
        settings.Aio.Widgets = [new() { Source = WidgetSource.Text, TextTemplate = "AIO BEFORE", X = 8, Y = 8, Width = 460, Height = 100 }];
        int panelOpens = 0, aioOpens = 0, wrongBrightness = 0;
        var panel = new TestLcd(); var aio = new TestLcd();
        var engine = new Engine(_ => throw new InvalidOperationException("No fan controller should open"), () => telemetry with { Time = DateTimeOffset.UtcNow }, () => [], displayFactory: (isPanel, brightness) =>
        {
            if (isPanel) { Interlocked.Increment(ref panelOpens); if (brightness != 43) Interlocked.Increment(ref wrongBrightness); return panel; }
            Interlocked.Increment(ref aioOpens); if (brightness != 67) Interlocked.Increment(ref wrongBrightness); return aio;
        });
        engine.Start(settings, true);
        try
        {
            Check(Wait(() => Volatile.Read(ref panel.Frames) > 0 && Volatile.Read(ref aio.Frames) > 0), "both physical-format frame pipelines start through fake transports");
            Check(panel.Width == 480 && panel.Height == 128 && aio.Width == 480 && aio.Height == 480, "each transport receives valid JPEG at the correct native size");
            Check(wrongBrightness == 0 && panelOpens == 1 && aioOpens == 1, "each transport receives its own brightness and one connection");
            string beforePanel = Volatile.Read(ref panel.Hash), beforeAio = Volatile.Read(ref aio.Hash);
            var edited = settings.Copy(); edited.Panel.Widgets[0].TextTemplate = "PANEL AFTER"; edited.Aio.Widgets[0].TextTemplate = "AIO AFTER";
            engine.ApplyLiveSettings(edited);
            Check(Wait(() => Volatile.Read(ref panel.Hash) != beforePanel && Volatile.Read(ref aio.Hash) != beforeAio), "both changed widget layouts reach the next frames without restarting");
            Check(panelOpens == 1 && aioOpens == 1 && !engine.ControllerFault, "layout edits do not reconnect either LCD or invent a fan fault");
            Check(Volatile.Read(ref panel.Heartbeats) > 0 && Volatile.Read(ref aio.Heartbeats) > 0, "both display workers send heartbeats");
        }
        finally { Task.Run(async () => await engine.Stop()).GetAwaiter().GetResult(); }
        Check(panel.Disposals == 1 && aio.Disposals == 1, "Stop disposes each successful transport exactly once");
        int stoppedFrames = panel.Frames + aio.Frames; Thread.Sleep(50);
        Check(panel.Frames + aio.Frames == stoppedFrames && !engine.Running, "Stop leaves no background frame writers");

        int fanOpens = 0, retryPanel = 0, retryAio = 0;
        var fan = new TestController(); var recoveredPanel = new TestLcd(); var failedAio = new TestLcd { FailHeartbeat = true }; var recoveredAio = new TestLcd();
        var retrySettings = settings.Copy(); retrySettings.CoolingEnabled = retrySettings.CoolingWiringConfirmed = true;
        retrySettings.Ports[0].Role = PortRole.Fan; retrySettings.Ports[0].Mode = ControlMode.Fixed; retrySettings.Ports[0].FixedDuty = 40;
        var retry = new Engine(_ => { Interlocked.Increment(ref fanOpens); return fan; }, () => telemetry with { Time = DateTimeOffset.UtcNow, CpuTemperature = 40, GpuTemperature = 40 }, () => [], displayFactory: (isPanel, _) =>
        {
            if (isPanel) { if (Interlocked.Increment(ref retryPanel) < 3) throw new IOException("Injected absent chassis LCD"); return recoveredPanel; }
            return Interlocked.Increment(ref retryAio) == 1 ? failedAio : recoveredAio;
        }, lcdRetryDelay: TimeSpan.FromMilliseconds(50));
        retry.Start(retrySettings, true);
        try
        {
            Check(Wait(() => recoveredPanel.Frames > 0 && recoveredAio.Frames > 0 && fan.DutyWrites > 0), "temporarily absent panel and failed AX heartbeat independently recover");
            Check(retryPanel == 3 && retryAio == 2 && failedAio.Disposals == 1, "failed display instance is disposed before a bounded retry");
            Check(fanOpens == 1 && fan.FirstDuty == 40 && fan.LastDuty == 40 && !retry.ControllerFault, "LCD failures cannot restart or accelerate mock cooling");
            Check(retry.Logs.Any(line => line.Contains("Injected absent chassis LCD")) && retry.Logs.Any(line => line.Contains("Injected LCD heartbeat failure")), "display faults remain visible in diagnostics");
        }
        finally { Task.Run(async () => await retry.Stop()).GetAwaiter().GetResult(); }
        Check(recoveredPanel.Disposals == 1 && recoveredAio.Disposals == 1 && failedAio.Disposals == 1, "all connected and failed displays close exactly once");

        int absentAttempts = 0;
        var cancelSettings = new Settings(); cancelSettings.Aio.Enabled = true;
        var cancel = new Engine(_ => throw new InvalidOperationException("No controller access"), () => telemetry with { Time = DateTimeOffset.UtcNow }, () => [], displayFactory: (_, _) => { Interlocked.Increment(ref absentAttempts); throw new IOException("Injected unplugged display"); });
        cancel.Start(cancelSettings, true);
        Check(Wait(() => Volatile.Read(ref absentAttempts) == 1), "absent LCD enters retry delay");
        var stopping = Stopwatch.StartNew(); Task.Run(async () => await cancel.Stop()).GetAwaiter().GetResult();
        Check(stopping.Elapsed < TimeSpan.FromSeconds(2) && absentAttempts == 1 && !cancel.Running, "cancellation interrupts five-second display retry without a second open");

        int uncertainOpens = 0;
        var uncertain = new TestLcd { FailHeartbeat = true, FailDispose = true }; var stablePanel = new TestLcd(); var unaffectedFan = new TestController();
        var uncertainEngine = new Engine(_ => unaffectedFan, () => telemetry with { Time = DateTimeOffset.UtcNow, CpuTemperature = 40, GpuTemperature = 40 }, () => [], displayFactory: (isPanel, _) =>
        { if (isPanel) return stablePanel; Interlocked.Increment(ref uncertainOpens); return uncertain; }, lcdRetryDelay: TimeSpan.FromMilliseconds(50));
        uncertainEngine.Start(retrySettings, true);
        try
        {
            Check(Wait(() => stablePanel.Frames >= 3 && uncertain.Disposals == 1 && unaffectedFan.DutyWrites > 0), "display close fault does not stop the other display or cooling");
            Check(uncertainOpens == 1 && uncertainEngine.Status["TOUGHLIQUID AX"].Contains("retry blocked") && !uncertainEngine.ControllerFault, "uncertain USB release latches only its display, without blind reconnect");
            Check(unaffectedFan.LastDuty == 40, "display release error cannot produce full-speed fan fallback");
        }
        finally { Task.Run(async () => await uncertainEngine.Stop()).GetAwaiter().GetResult(); }
        Check(uncertain.Disposals == 1 && stablePanel.Disposals == 1, "Stop does not repeatedly dispose an uncertain failed display");
        var brokenFrames = new TestLcd { FailDispose = true }; var commands = new TestLcd();
        bool pairFailure = false; try { PanelLcd.DisposeInterfaces(brokenFrames, commands); } catch (IOException) { pairFailure = true; }
        Check(pairFailure && brokenFrames.Disposals == 1 && commands.Disposals == 1, "both chassis interfaces receive close even when the first fails");
        return count;
    }
    private sealed class TestLcd : ILcd
    {
        public int Frames, Heartbeats, Disposals, Width, Height;
        public string Hash = "";
        public bool FailHeartbeat, FailDispose;
        public void Send(byte[] jpeg)
        {
            using var stream = new MemoryStream(jpeg, writable: false); using var image = Image.FromStream(stream);
            Width = image.Width; Height = image.Height; Volatile.Write(ref Hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(jpeg))); Interlocked.Increment(ref Frames);
        }
        public void Heartbeat() { if (FailHeartbeat) throw new IOException("Injected LCD heartbeat failure"); Interlocked.Increment(ref Heartbeats); }
        public void Dispose() { Interlocked.Increment(ref Disposals); if (FailDispose) throw new IOException("Injected LCD close failure"); }
    }
    private sealed class TestController : IFanController
    {
        public int DutyWrites, LightingFrames, BlackFrames, LastDuty, FirstDuty = -1;
        public void SetDuty(int port, int duty) { Interlocked.Increment(ref DutyWrites); Interlocked.CompareExchange(ref FirstDuty, duty, -1); LastDuty = duty; }
        public void SetRgbFrame(int port, IReadOnlyList<LedColor> colors) { Interlocked.Increment(ref LightingFrames); if (colors.All(c => c == new LedColor(0, 0, 0))) Interlocked.Increment(ref BlackFrames); }
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
