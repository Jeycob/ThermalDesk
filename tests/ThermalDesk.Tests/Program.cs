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
Console.WriteLine($"PASS: {tests} assertions. Hardware-independent tests; no USB device was opened.");
