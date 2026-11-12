using System.Security.Principal;
using System.Text.Json;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// Explicit, bounded acceptance diagnostics. Never selected by ordinary application startup.
internal static class HardwareChecks
{
    public static int Run(string mode, string output)
    {
        var report = new Dictionary<string, object?>
        {
            ["time"] = DateTimeOffset.UtcNow, ["mode"] = mode,
            ["administrator"] = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            ["fanSpeedWrites"] = false, ["driverInstalledByThisCheck"] = false
        };
        int exit = 0;
        try
        {
            if (mode == "--sensor-check")
            {
                using var monitor = new Monitoring();
                var readings = new List<Telemetry>();
                for (int i = 0; i < 4; i++) { readings.Add(monitor.Read()); Thread.Sleep(1000); }
                report["readings"] = readings;
                bool available = readings.Any(r => r.CpuTemperature is >= 5 and <= 115);
                report["cpuTemperatureAvailable"] = available;
                if (!available) exit = 2;
                report["usbOpened"] = false;
            }
            else if (mode == "--controller-profile-check")
            {
                if (Engine.Conflicts().Length != 0 || ProcessRunning()) throw new InvalidOperationException("Close other hardware-control applications first.");
                var profile = SettingsStore.Load(Path.GetFullPath(output) + ".profile.json");
                if (!profile.CoolingEnabled || !profile.CoolingWiringConfirmed || profile.Ports.Any(p => p.Role != PortRole.Fan || p.Mode == ControlMode.Preserve))
                    throw new ArgumentException("This check requires an explicitly configured five-fan profile. Pumps and unknown wiring are excluded.");
                using (var monitor = new Monitoring())
                {
                    var t = monitor.Read(profile.PreferredGpuId);
                    if (t.CpuTemperature is not (>= 5 and < 80) || t.GpuTemperature is not (>= 5 and < 80) || t.CpuMaximumTemperature is >= 80) throw new InvalidOperationException("Valid selected-device temperatures under 80 C required before starting.");
                }
                var engine = new Engine(); int applies = 0; var samples = new List<object>();
                report["fanSpeedWrites"] = true;
                try
                {
                    engine.Start(profile, true);
                    for (int second = 0; second < 90; second++)
                    {
                        Thread.Sleep(1000);
                        if (engine.ControllerFault || engine.Cancelled) throw new IOException("Controller fault or conflict; test stopped without reconnecting.");
                        var t = engine.Latest;
                        if (second > 8 && (t.CpuTemperature is not (>= 5 and < 85) || t.GpuTemperature is not (>= 5 and < 85) || FanSafety.IsCritical(t, DateTimeOffset.UtcNow)))
                            throw new IOException("Temperatures unavailable/critical; stopping and requesting captured duties, except a real critical-heat override.");
                        if (second % 5 == 0) samples.Add(new { second, t.CpuTemperature, t.GpuTemperature, fans = engine.Fans.Values.OrderBy(x => x.Port).ToArray(), status = engine.Status.ToDictionary() });
                        // Exercise in-place UI-equivalent changes, not session restarts.
                        if (second == 30)
                        { var full = profile.Copy(); foreach (var p in full.Ports) FanPresets.Apply(p, FanPreset.Full); engine.ApplyLiveSettings(full); applies++; }
                        if (second == 40) { engine.ApplyLiveSettings(profile); applies++; }
                        if (second == 65)
                        { var lighting = profile.Copy(); lighting.Ports[0].RgbEffect = RgbEffect.Breathing; engine.ApplyLiveSettings(lighting); applies++; }
                        if (second == 80) { engine.ApplyLiveSettings(profile); applies++; }
                    }
                    report["controllerConnectedCount"] = engine.Logs.Count(x => x.Contains("Controller: Connected"));
                    if (engine.Fans.Count != 5 || (int)report["controllerConnectedCount"]! != 1) throw new IOException("Missing fan feedback or an unexpected controller reopen.");
                }
                finally
                {
                    report["samples"] = samples; report["log"] = engine.Logs; report["inPlaceApplies"] = applies;
                    engine.Stop().GetAwaiter().GetResult();
                    report["endPolicy"] = "Captured original duties requested back; not BIOS hand-back. No forced full-speed fallback.";
                }
                report["commands"] = "90-second explicit five-fan profile + AX LCD test: Custom -> Full -> Custom; port-1 lighting animation -> saved lighting. Four in-place updates, no other controller or BIOS/pump commands.";
                report["physicalColorOrLcdConfirmed"] = false;
            }
            else if (mode == "--controller-stability-check")
            {
                if (Engine.Conflicts().Length != 0 || ProcessRunning()) throw new InvalidOperationException("Close hardware-control applications before this attended test.");
                using var monitor = new Monitoring();
                Telemetry Guard()
                {
                    var t = monitor.Read();
                    if (t.CpuTemperature is not (>= 5 and < 85) || t.GpuTemperature is not (>= 5 and < 85) || FanSafety.IsCritical(t, DateTimeOffset.UtcNow))
                        throw new InvalidOperationException("CPU/GPU core must be valid and below 85 C.");
                    return t;
                }
                Guard(); using var controller = new FanController("");
                var baseline = controller.Read(1); report["baseline"] = baseline;
                if (baseline.Rpm <= 0 || baseline.Duty is < 30 or > 100) throw new IOException("Invalid baseline.");
                int target = Math.Max(70, baseline.Duty); controller.SetDuty(1, target); report["fanSpeedWrites"] = true;
                var port = new PortSettings { Port = 1, LedCount = 24, RgbColor = "23D6BA", RgbBrightness = 40 };
                var samples = new List<object>(); int frames = 0;
                Thread.Sleep(2000);
                var clock = System.Diagnostics.Stopwatch.StartNew(); double nextRead = 0; var telemetry = Guard();
                while (clock.Elapsed.TotalSeconds < 40)
                {
                    double seconds = clock.Elapsed.TotalSeconds;
                    if (seconds >= nextRead)
                    {
                        telemetry = Guard(); var readings = Enumerable.Range(1, 5).Select(controller.Read).ToArray();
                        samples.Add(new { seconds, phase = seconds < 5 ? "telemetry-only" : seconds < 20 ? "static-rgb" : "animated-rgb", readings, controller.SkippedReports });
                        File.WriteAllText(Path.GetFullPath(output) + ".progress.json", JsonSerializer.Serialize(new { seconds, readings, controller.SkippedReports }, Settings.JsonOptions));
                        nextRead = clock.Elapsed.TotalSeconds + 2;
                        if (readings[0].Duty != target || readings[0].Rpm <= 0) throw new IOException("Port 1 feedback changed unexpectedly during the combined test.");
                    }
                    if (seconds >= 5)
                    {
                        port.RgbEffect = seconds < 20 ? RgbEffect.Static : RgbEffect.Breathing;
                        controller.SetRgbFrame(1, Lighting.Frame(port, "23D6BA", seconds, telemetry)); frames++;
                    }
                    Thread.Sleep(200);
                }
                Guard(); report["restored"] = controller.RestoreAcceptanceDuty(1, baseline.Duty);
                report["samples"] = samples; report["rgbFrames"] = frames; report["skippedReports"] = controller.SkippedReports;
                report["commands"] = "40-second combined port-1 fan/RGB test with all-port readback. Never below baseline; original duty restored and confirmed. No controller reconnect, other-port speed or BIOS/pump command.";
                report["physicalColorConfirmed"] = false;
            }
            else if (mode == "--fan-curve-acceptance")
            {
                if (Engine.Conflicts().Length != 0) throw new InvalidOperationException("Close TT and other controllers before the attended test.");
                using var monitor = new Monitoring();
                static void GuardTemperature(Telemetry t)
                {
                    if (t.CpuTemperature is not (>= 5 and < 85) || t.GpuTemperature is not (>= 5 and < 85))
                        throw new InvalidOperationException("Valid CPU/GPU core temperatures below 85 C are required; test aborted.");
                }
                GuardTemperature(monitor.Read());
                using var controller = new FanController("");
                var original = controller.Read(1);
                if (original.Rpm <= 0 || original.Duty is < 30 or > 100) throw new InvalidOperationException("Port 1 baseline is not a running fan at a valid duty.");
                report["baseline"] = original;
                report["fanSpeedWrites"] = true;
                report["testedPort"] = 1;
                var stages = new List<object>();
                bool safeToRestore = false;
                try
                {
                    foreach (int startDuty in new[] { 60, 80 })
                    {
                        var port = new PortSettings { Port = 1, Role = PortRole.Fan, Mode = ControlMode.Curve, Source = TemperatureSource.Cpu,
                            Curve = [new(30, startDuty), new(80, Math.Min(startDuty + 20, 100))] };
                        var telemetry = monitor.Read(); GuardTemperature(telemetry);
                        int target = Math.Max(original.Duty, new FanSafety().Evaluate(port, telemetry, DateTimeOffset.UtcNow, 2).Duty!.Value);
                        controller.SetDuty(1, target);
                        for (int i = 0; i < 4; i++) { Thread.Sleep(1000); GuardTemperature(monitor.Read()); }
                        var reading = controller.Read(1);
                        stages.Add(new { Curve = port.Curve, RequestedDuty = target, Reading = reading, telemetry.CpuTemperature, telemetry.GpuTemperature });
                        if (reading.Duty != target || reading.Rpm <= 0) throw new IOException("Fan telemetry did not confirm the requested curve duty.");
                    }
                    GuardTemperature(monitor.Read()); safeToRestore = true;
                }
                finally
                {
                    report["stages"] = stages;
                    if (safeToRestore) report["restored"] = controller.RestoreAcceptanceDuty(1, original.Duty);
                    else report["restorePolicy"] = "Test failed: captured baseline requested back on disposal; restore TT before unattended use.";
                }
                report["commands"] = "Attended port-1 curve test; never below original duty, then original duty restored and confirmed. No other port, RGB or pump command.";
            }
            else
            {
                var conflicts = Engine.Conflicts();
                if (conflicts.Length != 0) throw new InvalidOperationException("Close conflicting controllers first: " + string.Join(", ", conflicts));
                report["inventory"] = HidLink.Inventory();
                if (mode == "--fan-status-check")
                {
                    // The documented initialization handshake and telemetry requests only.
                    // Preserve every channel; no call to SetDuty, SetRgb or managed-port registration.
                    using var controller = new FanController("");
                    var ports = new List<object>();
                    for (int port = 1; port <= 5; port++)
                        try { ports.Add(controller.Read(port)); }
                        catch (Exception e) { ports.Add(new { Port = port, Error = e.Message }); }
                    report["ports"] = ports;
                    report["commands"] = "Controller initialization and status queries only; no duty or RGB writes.";
                }
                else if (mode == "--aio-firmware-check")
                {
                    var device = HidLink.Single(Protocols.AioPid);
                    if (device.GetMaxFeatureReportLength() != 64) throw new NotSupportedException("Unexpected AX feature descriptor.");
                    using var link = new HidLink(device, 1024);
                    byte[] reply = link.GetFeature(0x0a);
                    report["firmwareReplyHex"] = Convert.ToHexString(reply);
                    report["recognizedAxFirmware"] = Protocols.IsAxFirmware(reply);
                    report["commands"] = "Firmware feature GET only; no configuration, RGB or duty writes.";
                }
                else if (mode is "--aio-display-check" or "--panel-display-check")
                {
                    bool panel = mode == "--panel-display-check";
                    var settings = new DisplaySettings { Enabled = true, Brightness = 70 };
                    using var lcd = panel ? (ILcd)new PanelLcd(70) : new AioLcd(70);
                    using var renderer = new FrameRenderer();
                    using var monitor = new Monitoring();
                    int sent = 0;
                    // Keep the link/heartbeat alive while the operator observes the actual LCD.
                    // A five-second transfer ending before confirmation is not an adequate display test.
                    for (int i = 0; i < 60; i++)
                    {
                        using var frame = renderer.Render(monitor.Read(), settings, 480, panel ? 128 : 480);
                        lcd.Send(FrameRenderer.Jpeg(frame)); lcd.Heartbeat(); sent++; Thread.Sleep(1000);
                    }
                    report["framesSent"] = sent;
                    report["physicalImageConfirmed"] = false;
                    report["commands"] = "LCD initialization, brightness 70%, sixty dashboard frames and heartbeat. No fan-speed or RGB writes.";
                }
                else throw new ArgumentException("Unknown hardware check.");
            }
        }
        catch (Exception e) { report["error"] = e.ToString(); exit = 2; }
        report["exitCode"] = exit;
        string json = JsonSerializer.Serialize(report, Settings.JsonOptions);
        File.WriteAllText(Path.GetFullPath(output), json);
        Console.WriteLine(json);
        return exit;
    }
    private static bool ProcessRunning() => System.Diagnostics.Process.GetProcessesByName("ThermalDesk").Any(p =>
    { using (p) return p.Id != Environment.ProcessId; });
}
