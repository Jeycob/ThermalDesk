using System.Buffers.Binary;
using System.Text;
using ThermalDesk.Core;

int tests = 0;
void Check(bool value, string name) { tests++; if (!value) throw new Exception("FAILED: " + name); }
void Throws(Action a, string name) { try { a(); } catch (ArgumentException) { Check(true, name); return; } throw new Exception("FAILED: " + name); }
var now = DateTimeOffset.UtcNow;
Telemetry Tele(double? c = 50, double? g = 40) => new(now, c, g, 10, 5, 30);
var port = new PortSettings { Port = 1, Mode = ControlMode.Curve, Role = PortRole.Fan };
var safety = new FanSafety();
Check(safety.Evaluate(port, Tele(), now, 2).Duty == 50, "curve interpolation");
Check(safety.Evaluate(port, Tele(86), now, 2).Duty == 100, "CPU critical immediate");
Check(safety.Evaluate(port, Tele(40, 90), now, 2).Duty == 100, "GPU critical immediate");
Check(safety.Evaluate(port, Tele(null), now, 2).Duty == 100, "missing CPU");
Check(safety.Evaluate(port, Tele(double.NaN), now, 2).Duty == 100, "NaN CPU");
Check(safety.Evaluate(port, Tele(0), now, 2).Duty == 100, "zero CPU sensor sentinel");
Check(safety.Evaluate(port, Tele(-5), now, 2).Duty == 100, "implausible CPU sensor");
Check(safety.Evaluate(port, Tele(40, double.PositiveInfinity), now, 2).Duty == 100, "invalid max-source GPU");
Check(safety.Evaluate(port, Tele() with { Time = now.AddSeconds(-6) }, now, 2).Duty == 100, "stale data");
Check(safety.Evaluate(port, Tele() with { Time = now.AddSeconds(10) }, now, 2).Duty == 100, "future data rejected");
port.Mode = ControlMode.Fixed; port.FixedDuty = 30; port.Role = PortRole.Pump; safety.Reset();
Check(safety.Evaluate(port, Tele(), now, 2).Duty == 60, "pump hard minimum");
Check(safety.Evaluate(port, Tele(null), now, 2).Duty == 100, "fixed-mode CPU missing");
port.Role = PortRole.Fan; port.FixedDuty = 70; safety.Reset();
Check(safety.Evaluate(port, Tele(), now, 2).Duty == 70, "fixed duty");
port.FixedDuty = 40;
Check(safety.Evaluate(port, Tele(), now, 2).Duty == 64, "slow downward ramp");
port.FixedDuty = 90;
Check(safety.Evaluate(port, Tele(), now, 2).Duty == 90, "immediate upward response");
port.Mode = ControlMode.Preserve; port.Role = PortRole.Unknown;
Check(safety.Evaluate(port, Tele(100), now, 2).Duty is null, "preserve never writes even at critical temperature");
port = new() { Port = 1, Role = PortRole.Fan, Mode = ControlMode.Curve, Source = TemperatureSource.Cpu };
safety.Reset(); var initial = safety.Evaluate(port, Tele(60), now, 2).Duty;
Check(safety.Evaluate(port, Tele(59), now, 2).Duty == initial, "2 degree downward hysteresis");
Check(safety.Evaluate(port, Tele(50), now, 2).Duty < initial, "hysteresis release");
var settings = new Settings(); settings.Validate(); Check(!settings.CoolingEnabled && !settings.Panel.Enabled && !settings.Aio.Enabled, "all writes default off");
Throws(settings.ValidateLiveSelection, "empty live session rejected");
settings.Aio.Enabled = true; settings.ValidateLiveSelection(); Check(true, "explicit LCD is a valid live selection"); settings.Aio.Enabled = false;
settings.CoolingEnabled = true; Throws(settings.Validate, "arming requires wiring and configured port");
settings.CoolingWiringConfirmed = true; settings.Ports[0].Mode = ControlMode.Curve; Throws(settings.Validate, "unknown port role rejected");
settings.Ports[0].Role = PortRole.Fan; settings.Validate(); Check(true, "valid armed profile");
settings.Ports[0].Curve = [new(50, 50), new(40, 60)]; Throws(settings.Validate, "unsorted curve rejected");
settings = new(); settings.Ports[0].Curve = [new(40, 60), new(50, 50)]; Throws(settings.Validate, "decreasing duty rejected");
settings = new(); settings.Ports[1].Port = 1; Throws(settings.Validate, "duplicate port rejected");
settings = new(); settings.PollSeconds = 11; Throws(settings.Validate, "bounded polling");
settings = new(); settings.Panel.Rotation = 42; Throws(settings.Validate, "invalid rotation rejected");
settings = new(); settings.Panel.FramesPerSecond = 50; Throws(settings.Validate, "unbounded FPS rejected");
settings = new(); settings.RgbEnabled = true; Throws(settings.Validate, "RGB requires explicit selected port");
settings = new(); var copy = settings.Copy(); copy.Ports[0].MinimumDuty = 80; Check(settings.Ports[0].MinimumDuty == 35, "deep copy isolates session settings");
var path = Path.Combine(Path.GetTempPath(), "thermaldesk-tests-" + Guid.NewGuid().ToString("N"), "profile.json");
try { SettingsStore.Save(path, settings); var read = SettingsStore.Load(path); Check(read.Ports.Count == 5 && !read.CoolingEnabled, "atomic profile roundtrip"); }
finally { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
var random = new Random(9042);
foreach (int length in new[] { 4, 1016, 1017, 1020, 1021, 5000, 200000 })
{
    var jpeg = new byte[length]; random.NextBytes(jpeg); jpeg[0] = 255; jpeg[1] = 216; jpeg[^2] = 255; jpeg[^1] = 217;
    var panel = Protocols.PanelJpeg(jpeg);
    Check(panel.All(p => p.Length == 1025 && p[0] == 0 && p[1] == 8), $"panel lengths {length}");
    Check(panel[0][2] == panel.Count && panel[0][4] == 128, $"panel first header {length}");
    Check(panel.SelectMany(p => p.Skip(5)).Take(length).SequenceEqual(jpeg), $"panel reconstruction {length}");
    var aio = Protocols.AioJpeg(jpeg);
    Check(aio.All(p => p.Length == 1024 && p[0] == 2 && p[1] == 9), $"AX lengths {length}");
    Check(aio[^1][3] == 1 && aio.Take(aio.Count - 1).All(p => p[3] == 0), $"AX last flag {length}");
    Check(aio.SelectMany(p => p.Skip(8).Take(BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(4)))).SequenceEqual(jpeg), $"AX reconstruction {length}");
    Check(aio.Select((p, i) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(6)) == i).All(x => x), $"AX sequence {length}");
}
Throws(() => Protocols.PanelJpeg([0, 0, 0, 0]), "bad JPEG rejected");
var oversized = new byte[260101]; oversized[0] = 255; oversized[1] = 216; oversized[^2] = 255; oversized[^1] = 217;
Throws(() => Protocols.PanelJpeg(oversized), "panel packet count overflow rejected");
var fw = new byte[64]; fw[0] = 10; Encoding.ASCII.GetBytes("1.0.5.4").CopyTo(fw, 6);
Check(Protocols.IsAxFirmware(fw), "known AX firmware shape"); fw[0] = 11; Check(!Protocols.IsAxFirmware(fw), "unknown firmware refused");
var info = new byte[64]; info[0] = 7; BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(7), 480); BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(9), 480);
Check(Protocols.AxSize(info) == (480, 480), "AX resolution decoded");
var rgb = Protocols.Rgb(5, 62, "123456"); Check(rgb.Length == 193 && rgb[3] == 5 && rgb[4] == 0x24 && rgb[5] == 0x34 && rgb[6] == 0x12 && rgb[7] == 0x56, "RGB packet / GRB order");
Throws(() => Protocols.Rgb(1, 63, "FFFFFF"), "RGB overflow rejected");
Throws(() => Protocols.SetDuty(1, 0), "zero duty cannot be sent");
Throws(() => Protocols.SetDuty(6, 100), "invalid port rejected");
var duty = Protocols.SetDuty(2, 80); Check(duty[1] == 0x32 && duty[2] == 0x51 && duty[3] == 2 && duty[4] == 1 && duty[5] == 80, "duty command");
var reply = new byte[] { 0, 0x33, 0x51, 2, 0, 80, 0x34, 0x12 }; Check(Protocols.ParseFan(reply, 2).Rpm == 0x1234, "RPM little endian");
Throws(() => Protocols.ParseFan(reply, 3), "cross-port reply refused"); reply[1] = 0; Throws(() => Protocols.ParseFan(reply, 2), "unrelated reply refused");
foreach (var physical in new[] { (480, 128), (480, 480) })
foreach (int rotation in new[] { 0, 90, 180, 270 })
{
    var display = new DisplaySettings { UseCustomLayout = true, Rotation = rotation };
    var size = WidgetLayout.Canvas(display, physical.Item1, physical.Item2);
    display.Widgets = WidgetLayout.Defaults(size.Width, size.Height);
    WidgetLayout.Validate(display, physical.Item1, physical.Item2); Check(true, $"valid widget template {physical} rotation {rotation}");
    var saved = display.Copy(); saved.Widgets[0].TextTemplate = "changed";
    Check(display.Widgets[0].TextTemplate != "changed", "widget deep copy");
}
var widgets = new DisplaySettings { UseCustomLayout = true, Widgets = WidgetLayout.Defaults(480, 480) };
var widget = widgets.Widgets[2]; widget.ValueFormat = "0.0";
Check(WidgetLayout.Format(widget, Tele(52.25)) == "52.3°", "numeric widget format uses invariant culture");
Check(WidgetLayout.Format(widget, Tele(null)) == "—°", "missing sensor placeholder");
Check(WidgetLayout.Format(widget, Tele(double.NaN)) == "—°", "NaN never displayed as a reading");
widget.Source = WidgetSource.FanRpm; widget.Port = 2; widget.TextTemplate = "Fan {value}"; widget.ValueFormat = "0";
Check(WidgetLayout.Format(widget, Tele() with { Fans = [new(2, 1234, 60)] }) == "Fan 1234", "fan port source binding");
Check(WidgetLayout.Format(widget, Tele()) == "Fan —", "missing fan placeholder");
widget.Source = WidgetSource.GpuHotSpotTemperature;
Check(WidgetLayout.Format(widget, Tele() with { GpuHotSpotTemperature = 83 }) == "Fan 83", "hotspot is a separate display source");
widget.Source = WidgetSource.Text; widget.TextTemplate = "literal {value}";
Check(WidgetLayout.Format(widget, Tele()) == "literal {value}", "text source never evaluates placeholders");
widget.Source = WidgetSource.Clock; widget.TextTemplate = "{value}"; widget.ValueFormat = "HH:mm:ss";
Check(WidgetLayout.Format(widget, Tele()) == now.LocalDateTime.ToString("HH:mm:ss"), "clock uses snapshot time");
widget.Width = 481; Throws(() => WidgetLayout.Validate(widgets, 480, 480), "off-canvas geometry rejected"); widget.Width = 360;
widget.Foreground = "not-a-color"; Throws(() => WidgetLayout.Validate(widgets, 480, 480), "bad widget color rejected"); widget.Foreground = "FFFFFF";
widget.FontSize = float.PositiveInfinity; Throws(() => WidgetLayout.Validate(widgets, 480, 480), "infinite font rejected"); widget.FontSize = 36;
widget.Port = 6; Throws(() => WidgetLayout.Validate(widgets, 480, 480), "invalid source port rejected"); widget.Port = 1;
widget.ValueFormat = "%"; Throws(() => WidgetLayout.Validate(widgets, 480, 480), "invalid clock format rejected"); widget.ValueFormat = "HH:mm:ss";
widgets.Widgets[1].Id = widget.Id; Throws(() => WidgetLayout.Validate(widgets, 480, 480), "duplicate widget ID rejected"); widgets.Widgets[1].Id = Guid.NewGuid().ToString("N");
widgets.Widgets = Enumerable.Range(0, 41).Select(_ => new LcdWidget()).ToList(); Throws(() => WidgetLayout.Validate(widgets, 480, 480), "widget count bounded");
var rotated = new DisplaySettings { Widgets = WidgetLayout.Defaults(480, 128), Rotation = 90 };
WidgetLayout.Reframe(rotated, 480, 128, 128, 480); WidgetLayout.Validate(rotated, 480, 128); Check(true, "rotation reframes widgets into portrait canvas");
settings = new(); settings.Aio = new() { UseCustomLayout = true, Widgets = WidgetLayout.Defaults(480, 480) };
try
{
    SettingsStore.Save(path, settings); var roundtrip = SettingsStore.Load(path);
    Check(roundtrip.Aio.Widgets.Count == 6 && roundtrip.Aio.UseCustomLayout && roundtrip.Panel.Widgets.Count == 0, "independent LCD widget profile roundtrip");
    roundtrip.Aio.Widgets[0].Foreground = "112233";
    Check(settings.Aio.Widgets[0].Foreground != "112233", "roundtrip isolates widget edits");
}
finally { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
var lightPort = new PortSettings { Port = 1, LedCount = 12, RgbBrightness = 100, RgbColor = "FF0000" };
Check(Lighting.Frame(lightPort, "FFFFFF", 0, Tele()).All(c => c == new LedColor(255, 0, 0)), "static RGB frame");
lightPort.RgbBrightness = 50; Check(Lighting.Frame(lightPort, "FFFFFF", 0, Tele())[0].Red == 128, "brightness scaling"); lightPort.RgbBrightness = 100;
lightPort.RgbEffect = RgbEffect.Blink; Check(Lighting.Frame(lightPort, "FFFFFF", 3, Tele()).All(c => c == new LedColor(0, 0, 0)), "blink off phase");
lightPort.RgbEffect = RgbEffect.Breathing; Check(Lighting.Frame(lightPort, "FFFFFF", 0, Tele())[0].Red < Lighting.Frame(lightPort, "FFFFFF", 2, Tele())[0].Red, "breathing waveform");
lightPort.RgbEffect = RgbEffect.RainbowWave; Check(Lighting.Frame(lightPort, "FFFFFF", 0, Tele()).Distinct().Count() > 6, "rainbow addresses individual LEDs");
lightPort.RgbEffect = RgbEffect.Temperature; lightPort.Source = TemperatureSource.Cpu;
Check(Lighting.Frame(lightPort, "FFFFFF", 0, Tele(85))[0] == new LedColor(255, 0, 0), "critical RGB indication");
Check(Lighting.Frame(lightPort, "FFFFFF", 0, Tele(null))[0] == new LedColor(255, 160, 0), "missing sensor lighting is amber");
foreach (var effect in Enum.GetValues<RgbEffect>())
{
    lightPort.RgbEffect = effect;
    for (int i = 0; i < 50; i++)
    {
        var colors = Lighting.Frame(lightPort, "FFFFFF", i * .2, Tele());
        Check(colors.Length == 12, $"bounded RGB frame {effect}");
        var packet = Protocols.RgbFrame(1, colors); Check(packet.Length == 193 && packet[4] == 0x24 && packet[5] == colors[0].Green && packet[6] == colors[0].Red, "RGB protocol GRB order");
    }
}
lightPort.RgbPeriodSeconds = 0; Throws(() => Lighting.Frame(lightPort, "FFFFFF", 0, Tele()), "zero RGB period rejected");
Throws(() => Protocols.RgbFrame(0, [new(1, 2, 3)]), "RGB port zero rejected");
settings = new(); settings.Ports[0].RgbPeriodSeconds = double.NaN; Throws(settings.Validate, "NaN RGB profile rejected");
settings = new(); settings.Ports[0].RgbColor = "GGGGGG"; Throws(settings.Validate, "invalid RGB profile color rejected");
settings = new(); settings.StartLiveAtSignIn = true; Throws(settings.Validate, "auto live cannot arm an empty profile");
settings.Aio.Enabled = true; settings.Validate(); Check(true, "explicit saved LCD startup profile validates without enabling fans");
var portCopy = settings.Ports[0].Copy(); portCopy.RgbEffect = RgbEffect.RainbowWave; portCopy.Curve[0] = new(20, 30); Check(settings.Ports[0].Curve[0].Temperature == 30 && settings.Ports[0].RgbEffect == RgbEffect.Static, "port editor works on independent data");
Console.WriteLine($"PASS: {tests} assertions. Hardware-independent tests; no USB device was opened.");
