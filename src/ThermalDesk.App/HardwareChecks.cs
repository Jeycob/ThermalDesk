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
            ["fanSpeedWrites"] = false, ["driverInstalled"] = false
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
}
