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
Check(safety.Evaluate(port, Tele(null), now, 2).Duty is null, "missing CPU releases control, not forced 100%");
Check(safety.Evaluate(port, Tele(double.NaN), now, 2).Duty is null, "NaN CPU releases control");
Check(safety.Evaluate(port, Tele(0), now, 2).Duty is null, "zero CPU sensor sentinel");
Check(safety.Evaluate(port, Tele(-5), now, 2).Duty is null, "implausible CPU sensor");
Check(safety.Evaluate(port, Tele(40, double.PositiveInfinity), now, 2).Duty is null, "invalid max-source GPU");
Check(safety.Evaluate(port, Tele() with { Time = now.AddSeconds(-6) }, now, 2).Duty is null, "stale data releases without a full-speed command");
Check(safety.Evaluate(port, Tele() with { Time = now.AddSeconds(10) }, now, 2).Duty is null, "future data rejected");
Check(!FanSafety.IsCritical(Tele(150), now), "implausible sentinel cannot force emergency duty on close");
Check(!FanSafety.IsCritical(Tele(90) with { Time = now.AddSeconds(-6) }, now), "stale hot sample cannot force emergency duty on close");
Check(!FanSafety.IsCritical(Tele(90) with { Time = now.AddSeconds(2) }, now), "future hot sample cannot force emergency duty on close");
Check(FanSafety.IsCritical(Tele(null, 90), now), "valid fresh GPU overheat is a real emergency even if CPU is unavailable");
port.Mode = ControlMode.Fixed; port.FixedDuty = 30; port.Role = PortRole.Pump; safety.Reset();
Check(safety.Evaluate(port, Tele(), now, 2).Duty == 60, "pump hard minimum");
Check(safety.Evaluate(port, Tele(null), now, 2).Duty is null, "fixed-mode CPU missing releases control");
port.Role = PortRole.Fan; port.FixedDuty = 70; safety.Reset();
Check(safety.Evaluate(port, Tele(), now, 2).Duty == 70, "fixed duty");
port.FixedDuty = 40;
var ramp = safety.Evaluate(port, Tele(), now, 2);
Check(ramp.Duty == 64, "slow downward ramp");
Check(ramp.TargetDuty == 40, "final requested target remains visible during the downward ramp");
Check(new FanSafety().Evaluate(port, Tele(90), now, 2).TargetDuty == 100, "real critical response exposes the emergency target");
Check(new FanSafety().Evaluate(port, Tele(null), now, 2).TargetDuty is null, "missing sensor does not invent a target");
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
settings = new(); settings.Ports[0].Curve = [new(20, 30), new(30, 30), new(40, 33), new(50, 30), new(60, 30), new(70, 42), new(80, 59), new(90, 100), new(100, 100)]; settings.Validate(); Check(true, "TT custom dip preserved without sorting duty values");
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
    Check(roundtrip.Aio.Widgets.Count == 7 && roundtrip.Aio.UseCustomLayout && roundtrip.Panel.Widgets.Count == 0, "independent LCD widget profile roundtrip");
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
lightPort.RgbEffect = RgbEffect.PaletteWave; lightPort.LedCount = 4; lightPort.RgbPalette = ["FF0000", "0000FF"];
var gradient = Lighting.Frame(lightPort, "FFFFFF", 0, Tele());
Check(gradient.SequenceEqual(new LedColor[] { new(255, 0, 0), new(128, 0, 128), new(0, 0, 255), new(128, 0, 128) }), "palette interpolates and closes the LED ring smoothly");
var forward = Lighting.Frame(lightPort, "FFFFFF", 1, Tele());
lightPort.RgbDirection = RgbDirection.Reverse;
Check(forward.SequenceEqual(Lighting.Frame(lightPort, "FFFFFF", -1, Tele())), "reverse direction mirrors animation time");
Check(!forward.SequenceEqual(Lighting.Frame(lightPort, "FFFFFF", 1, Tele())), "wave direction changes movement");
Check(gradient.SequenceEqual(Lighting.Frame(lightPort, "FFFFFF", 4, Tele())), "palette wave is periodic");
lightPort.RgbBrightness = 0; Check(Lighting.Frame(lightPort, "FFFFFF", 1, Tele()).All(c => c == new LedColor(0, 0, 0)), "palette obeys zero brightness");
lightPort.RgbBrightness = 100; lightPort.LedCount = 12;
var paletteCopy = lightPort.Copy(); paletteCopy.RgbPalette[0] = "00FF00";
Check(lightPort.RgbPalette[0] == "FF0000", "copied palettes are independent");
foreach (var invalidPalette in new List<string>[] { [], ["FFFFFF"], ["nothex", "FFFFFF"], ["", "FFFFFF"], Enumerable.Repeat("FFFFFF", 9).ToList() })
{
    var invalid = lightPort.Copy(); invalid.RgbPalette = invalidPalette;
    Throws(() => Lighting.Frame(invalid, "FFFFFF", 0, Tele()), "invalid palette rejected before RGB frame");
    var invalidSettings = new Settings(); invalidSettings.Ports[0] = invalid;
    Throws(invalidSettings.Validate, "invalid palette profile rejected");
}
var invalidDirection = lightPort.Copy(); invalidDirection.RgbDirection = (RgbDirection)42;
Throws(() => Lighting.Frame(invalidDirection, "FFFFFF", 0, Tele()), "invalid direction rejected");
var legacyPort = System.Text.Json.JsonSerializer.Deserialize<PortSettings>("{\"Port\":1,\"RgbEffect\":\"Static\"}", Settings.JsonOptions)!;
Check(legacyPort.RgbPalette.Count == 0 && legacyPort.RgbDirection == RgbDirection.Forward, "old profiles keep legacy default lighting");
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
var backlog = new Queue<byte[]>();
for (int i = 0; i < 40; i++) backlog.Enqueue([0, 0x32, i % 2 == 0 ? (byte)0x52 : (byte)0x51, 1, 0, 90]);
backlog.Enqueue([0, 0x33, 0x51, 2, 0, 40, 10, 0]); // Wrong port is not a match.
backlog.Enqueue([0, 0x33, 0x51, 1, 0, 101, 10, 0]); // Invalid duty is not telemetry.
backlog.Enqueue([0, 0x33, 0x51, 1, 0, 60, 0x34, 0x12]);
int skipped = 0;
var freshFan = FanResponseReader.Read(_ => backlog.Dequeue(), 1, n => skipped = n);
Check(freshFan.Rpm == 0x1234 && skipped == 42 && backlog.Count == 0, "RGB/SET acknowledgement backlog is skipped until the complete telemetry header matches");
int noiseReads = 0; bool noiseBounded = false;
try { FanResponseReader.Read(_ => { noiseReads++; return [0, 0x32, 0x52, 1]; }, 1); }
catch (IOException) { noiseBounded = true; }
Check(noiseBounded && noiseReads == 128, "noisy endpoint has a hard packet budget");
bool timeoutReported = false;
try { FanResponseReader.Read(_ => throw new TimeoutException(), 1); }
catch (IOException e) { timeoutReported = e.Message.Contains("port 1"); }
Check(timeoutReported, "timeout becomes an actionable fault, never fake zero telemetry");
var presetPort = new PortSettings { Port = 1, Role = PortRole.Fan, MinimumDuty = 40 };
FanPresets.Apply(presetPort, FanPreset.Silent); Check(FanPresets.Identify(presetPort) == FanPreset.Silent && presetPort.MinimumDuty == 40, "Silent retains user safety minimum");
FanPresets.Apply(presetPort, FanPreset.Full); Check(presetPort.Mode == ControlMode.Fixed && presetPort.FixedDuty == 100, "Full requests 100%");
FanPresets.Apply(presetPort, FanPreset.Custom); Check(presetPort.Mode == ControlMode.Curve, "Custom exposes curve");
Check(presetPort.Curve.SequenceEqual(new PortSettings().Curve), "Custom restores the original curve after Silent and Full");
Throws(() => FanPresets.Apply(new PortSettings(), FanPreset.Silent), "preset cannot infer unknown wiring");
presetPort.Role = PortRole.Pump; FanPresets.Apply(presetPort, FanPreset.Silent); Check(presetPort.MinimumDuty >= 60, "Silent never bypasses pump floor");
var gaugeDisplay = new DisplaySettings { Widgets = WidgetLayout.Defaults(480, 480) };
var importedCurve = TtCurveImporter.Parse("30,30,33,30,30,42,59,100,100");
Check(importedCurve.Count == 9 && importedCurve[2] == new CurvePoint(40,33) && importedCurve[3] == new CurvePoint(50,30) && importedCurve[^1] == new CurvePoint(100,100), "TT nine points preserve temperature grid and dip exactly");
Throws(() => TtCurveImporter.Parse("1,2"), "incomplete TT curve rejected");
Throws(() => TtCurveImporter.Parse("30,30,30,30,30,30,30,30,NaN"), "nonfinite TT duty rejected");
Throws(() => TtCurveImporter.Parse("0,30,30,30,30,30,30,30,100"), "import cannot bypass minimum duty");
var gauge = gaugeDisplay.Widgets.Single(w => w.Kind == WidgetKind.RadialGauge);
Check(WidgetLayout.GaugeFraction(gauge, Tele(60)) == .6, "gauge maps temperature to progress");
Check(WidgetLayout.GaugeFraction(gauge, Tele(null)) == null, "missing gauge value is not a real zero reading");
Check(WidgetLayout.GaugeFraction(gauge, Tele(150)) == 1 && WidgetLayout.GaugeFraction(gauge, Tele(-5)) == 0, "gauge progress clamped");
gauge.GaugeMaximum = 0; Throws(() => WidgetLayout.Validate(gaugeDisplay, 480, 480), "zero gauge range rejected"); gauge.GaugeMaximum = 100;
gauge.Source = WidgetSource.Clock; Throws(() => WidgetLayout.Validate(gaugeDisplay, 480, 480), "non-numeric gauge source rejected"); gauge.Source = WidgetSource.CpuTemperature;
gauge.GaugeThickness = float.NaN; Throws(() => WidgetLayout.Validate(gaugeDisplay, 480, 480), "nonfinite gauge geometry rejected");
var sensorDevices = new HardwareSnapshot[]
{
    new("cpu", "Ryzen", DeviceCategory.Cpu, [new("Core (Tctl/Tdie)", SensorMetric.Temperature, 60), new("CPU CCD1 (Tdie)", SensorMetric.Temperature, 90), new("Distance to TjMax", SensorMetric.Temperature, 100), new("CPU Total", SensorMetric.Load, 12), new("CPU Core #1", SensorMetric.Load, 98)]),
    new("igpu", "Integrated AMD", DeviceCategory.AmdGpu, [new("GPU Core", SensorMetric.Temperature, 94), new("GPU Hot Spot", SensorMetric.Temperature, 110), new("GPU Memory", SensorMetric.Temperature, 105), new("GPU Core", SensorMetric.Load, 88)]),
    new("rtx", "NVIDIA RTX 4080", DeviceCategory.NvidiaGpu, [new("GPU Core", SensorMetric.Temperature, 43), new("GPU Hot Spot", SensorMetric.Temperature, 72), new("GPU Memory Junction", SensorMetric.Temperature, 82), new("GPU Core", SensorMetric.Load, 20), new("GPU Memory Controller", SensorMetric.Load, 99)]),
    new("ram", "RAM", DeviceCategory.Memory, [new("Memory", SensorMetric.Load, 35), new("Virtual Memory", SensorMetric.Load, 90)])
};
var sensorProjected = SensorProjection.Project(sensorDevices, now);
Check(sensorProjected.GpuDeviceId == "rtx" && sensorProjected.GpuDeviceName.Contains("4080"), "automatic GPU chooses NVIDIA, not hotter integrated graphics");
Check(sensorProjected.GpuTemperature == 43 && sensorProjected.GpuLoad == 20, "GPU core temperature and load stay on the selected device");
Check(sensorProjected.GpuHotSpotTemperature == 72 && sensorProjected.GpuMemoryTemperature == 82, "hotspot and VRAM are not borrowed from a different GPU");
Check(sensorProjected.CpuTemperature == 60 && sensorProjected.CpuTemperatureSource == "Core (Tctl/Tdie)", "CPU package is named explicitly rather than hottest unnamed sensor");
Check(sensorProjected.CpuMaximumTemperature == 90 && sensorProjected.CpuMaximumTemperatureSource == "CPU CCD1 (Tdie)", "actual CPU maximum retained separately; thermal distance is not a temperature");
Check(FanSafety.IsCritical(sensorProjected, now), "genuinely hot CPU CCD still triggers critical protection");
Check(!FanSafety.IsCritical(sensorProjected with { CpuMaximumTemperature = 60 }, now), "hot integrated GPU does not masquerade as selected GPU temperature");
Check(sensorProjected.CpuLoad == 12 && sensorProjected.MemoryUsedPercent == 35 && sensorProjected.MemoryLoadSource == "Memory", "total CPU and physical RAM loads, not busiest core or virtual memory");
Check(sensorProjected.GpuDevices.Count == 2 && sensorProjected.GpuDevices[0].Id == "rtx", "available GPU list is stable and selectable");
var sensorPinned = SensorProjection.Project(sensorDevices, now, "igpu");
Check(sensorPinned.GpuTemperature == 94 && sensorPinned.GpuLoad == 88 && sensorPinned.GpuHotSpotTemperature == 110, "explicit GPU selection controls every GPU metric");
var sensorMissing = SensorProjection.Project(sensorDevices, now, "disconnected");
Check(sensorMissing.GpuTemperature is null && sensorMissing.GpuLoad is null && sensorMissing.GpuDeviceId == "disconnected" && sensorMissing.Warning.Contains("no other GPU"), "missing pinned GPU does not silently fall back");
var noNvidiaCore = sensorDevices.Select(d => d.Id == "rtx" ? d with { Sensors = d.Sensors.Where(s => s.Metric != SensorMetric.Temperature || !s.Name.Contains("Core")).ToArray() } : d).ToArray();
var sensorNoCore = SensorProjection.Project(noNvidiaCore, now);
Check(sensorNoCore.GpuTemperature is null && sensorNoCore.GpuHotSpotTemperature == 72 && sensorNoCore.GpuMemoryTemperature == 82 && sensorNoCore.GpuDeviceId == "rtx", "missing core is unavailable, not hotspot or integrated GPU fallback");
Check(!FanSafety.IsCritical(sensorNoCore with { CpuMaximumTemperature = 60 }, now), "GPU hotspot is not mistaken for the core critical threshold");
var sensorInvalid = SensorProjection.Project([new("cpu", "CPU", DeviceCategory.Cpu, [new("CPU Package", SensorMetric.Temperature, 0), new("CPU Total", SensorMetric.Load, 101)]), new("gpu", "GPU", DeviceCategory.NvidiaGpu, [new("GPU Core", SensorMetric.Temperature, double.NaN), new("GPU Core", SensorMetric.Load, -1)])], now);
Check(sensorInvalid.CpuTemperature is null && sensorInvalid.GpuTemperature is null && sensorInvalid.CpuLoad is null && sensorInvalid.GpuLoad is null, "sentinel temperatures and out-of-range loads are unavailable");
Check(sensorInvalid.Warning.Contains("never forced to 100%"), "missing-sensor explanation matches quiet release policy");
Check(SensorProjection.Project(sensorDevices.Where(d => d.Category != DeviceCategory.NvidiaGpu).ToArray(), now).GpuDeviceId == "igpu", "automatic can use AMD when no NVIDIA device exists");
Check(WidgetLayout.NumericValue(new LcdWidget { Source = WidgetSource.CpuMaximumTemperature }, sensorProjected) == 90, "maximum CPU sensor can be selected as an LCD widget");
Check(!FanSafety.IsCritical(Tele() with { CpuMaximumTemperature = 150 }, now), "implausible maximum CPU sentinel cannot synthesize critical duty");
settings = new(); settings.PreferredGpuId = "rtx"; Check(settings.Copy().PreferredGpuId == "rtx", "preferred GPU survives profile copy");
settings.PreferredGpuId = "bad\nidentifier"; Throws(settings.Validate, "GPU identifier rejects control characters");
Console.WriteLine($"PASS: {tests} assertions. Hardware-independent tests; no USB device was opened.");
