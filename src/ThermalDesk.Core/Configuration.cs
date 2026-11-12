using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThermalDesk.Core;

public enum ControlMode { Preserve, Fixed, Curve }
public enum PortRole { Unknown, Fan, Pump }
public enum TemperatureSource { Cpu, Gpu, Maximum }
public enum DisplayContent { Dashboard, Image, Screen }
public enum RgbEffect { Static, Breathing, Blink, ColorCycle, RainbowWave, Temperature, PaletteWave }
public enum RgbDirection { Forward, Reverse }
public enum FanPreset { Silent, Custom, Full }
public sealed record CurvePoint(double Temperature, double Duty);
public sealed record Telemetry(DateTimeOffset Time, double? CpuTemperature, double? GpuTemperature,
    double? CpuLoad, double? GpuLoad, double? MemoryUsedPercent, string Warning = "")
{
    public double? GpuHotSpotTemperature { get; init; }
    public double? GpuMemoryTemperature { get; init; }
    public IReadOnlyList<FanReading> Fans { get; init; } = [];
}
public sealed record FanReading(int Port, int Rpm, int Duty);

public sealed class PortSettings
{
    public int Port { get; set; }
    public string Name { get; set; } = "";
    public PortRole Role { get; set; } = PortRole.Unknown;
    public ControlMode Mode { get; set; } = ControlMode.Preserve;
    public TemperatureSource Source { get; set; } = TemperatureSource.Maximum;
    public int MinimumDuty { get; set; } = 35;
    public int FixedDuty { get; set; } = 70;
    public bool Rgb { get; set; }
    public int LedCount { get; set; } = 12;
    public RgbEffect RgbEffect { get; set; } = RgbEffect.Static;
    public string RgbColor { get; set; } = "";
    public int RgbBrightness { get; set; } = 70;
    public double RgbPeriodSeconds { get; set; } = 4;
    public RgbDirection RgbDirection { get; set; } = RgbDirection.Forward;
    public List<string> RgbPalette { get; set; } = [];
    public List<CurvePoint> Curve { get; set; } = [new(30, 40), new(50, 50), new(65, 70), new(75, 90), new(80, 100)];
    public List<CurvePoint> CustomCurveBackup { get; set; } = [];
    public PortSettings Copy() => JsonSerializer.Deserialize<PortSettings>(JsonSerializer.Serialize(this, Settings.JsonOptions), Settings.JsonOptions)!;
}
public static class FanPresets
{
    public static FanPreset Identify(PortSettings p) => p.Mode == ControlMode.Fixed && p.FixedDuty == 100 ? FanPreset.Full
        : p.Mode == ControlMode.Curve && p.Curve.SequenceEqual(SilentCurve) ? FanPreset.Silent : FanPreset.Custom;
    private static readonly CurvePoint[] SilentCurve = [new(20, 30), new(60, 30), new(70, 40), new(80, 60), new(85, 100)];
    public static void Apply(PortSettings p, FanPreset preset)
    {
        if (!Enum.IsDefined(preset)) throw new ArgumentException("Unknown preset.");
        // Never infer wiring or silently convert a pump/unknown port into a fan.
        if (p.Role == PortRole.Unknown) throw new ArgumentException("Identify the wiring role first.");
        if (preset == FanPreset.Full) { p.Mode = ControlMode.Fixed; p.FixedDuty = 100; }
        else if (preset == FanPreset.Silent)
        {
            if (Identify(p) != FanPreset.Silent && p.CustomCurveBackup.Count == 0) p.CustomCurveBackup = p.Curve.ToList();
            p.Mode = ControlMode.Curve; p.Curve = SilentCurve.ToList();
            p.MinimumDuty = Math.Max(p.Role == PortRole.Pump ? 60 : 30, p.MinimumDuty);
        }
        else
        {
            p.Mode = ControlMode.Curve;
            if (p.CustomCurveBackup.Count > 0) { p.Curve = p.CustomCurveBackup.ToList(); p.CustomCurveBackup.Clear(); }
        }
    }
}
public sealed class DisplaySettings
{
    public bool Enabled { get; set; }
    public DisplayContent Content { get; set; } = DisplayContent.Dashboard;
    public string MediaPath { get; set; } = "";
    public int Rotation { get; set; }
    public int Brightness { get; set; } = 70;
    public bool Overlay { get; set; } = true;
    public int FramesPerSecond { get; set; } = 1;
    public bool UseCustomLayout { get; set; }
    public string BackgroundColor { get; set; } = "0A111D";
    public List<LcdWidget> Widgets { get; set; } = [];
    public DisplaySettings Copy() => JsonSerializer.Deserialize<DisplaySettings>(JsonSerializer.Serialize(this, Settings.JsonOptions), Settings.JsonOptions)!;
}
public sealed class Settings
{
    public int SchemaVersion { get; set; } = 1;
    public int PollSeconds { get; set; } = 2;
    public bool CoolingEnabled { get; set; }
    public bool CoolingWiringConfirmed { get; set; }
    public bool RgbEnabled { get; set; }
    public bool MonitorFanController { get; set; }
    public bool StartLiveAtSignIn { get; set; }
    public string RgbColor { get; set; } = "23D6BA";
    public string ControllerSerial { get; set; } = "";
    public DisplaySettings Panel { get; set; } = new();
    public DisplaySettings Aio { get; set; } = new();
    public List<PortSettings> Ports { get; set; } = Enumerable.Range(1, 5).Select(i => new PortSettings { Port = i }).ToList();

    public void Validate()
    {
        if (SchemaVersion != 1) throw new ArgumentException("Unsupported configuration version.");
        if (PollSeconds is < 1 or > 10) throw new ArgumentException("Polling must be 1–10 seconds.");
        if (Ports.Count != 5 || !Ports.Select(p => p.Port).Order().SequenceEqual(Enumerable.Range(1, 5)))
            throw new ArgumentException("Exactly five distinct controller ports (1–5) are required.");
        if (RgbColor.Length != 6 || !RgbColor.All(Uri.IsHexDigit)) throw new ArgumentException("RGB color must be six hexadecimal digits.");
        if (RgbEnabled && Ports.All(p => !p.Rgb)) throw new ArgumentException("Select at least one RGB port.");
        foreach (var p in Ports)
        {
            if (p.Name is null || p.Name.Length > 40 || p.Name.Any(char.IsControl)) throw new ArgumentException("Port names must be at most 40 printable characters.");
            if (!Enum.IsDefined(p.Role) || !Enum.IsDefined(p.Mode) || !Enum.IsDefined(p.Source)) throw new ArgumentException("Invalid port setting.");
            if (!Enum.IsDefined(p.RgbEffect) || p.RgbBrightness is < 0 or > 100 || !double.IsFinite(p.RgbPeriodSeconds) || p.RgbPeriodSeconds is < 1 or > 60 || !WidgetLayout.IsColor(p.RgbColor, true))
                throw new ArgumentException($"Port {p.Port}: invalid RGB effect, color, brightness (0–100%) or period (1–60 seconds).");
            Lighting.ValidatePalette(p);
            if (p.MinimumDuty is < 30 or > 100 || p.FixedDuty is < 30 or > 100 || p.LedCount is < 1 or > 62)
                throw new ArgumentException($"Port {p.Port}: duty must be 30–100%, LEDs 1–62.");
            if (p.Mode != ControlMode.Preserve && p.Role == PortRole.Unknown)
                throw new ArgumentException($"Identify fan/pump wiring on port {p.Port} before controlling it.");
            if (p.Curve.Count is < 2 or > 20 || p.Curve.Any(x => !double.IsFinite(x.Temperature) || !double.IsFinite(x.Duty) || x.Temperature is < 0 or > 100 || x.Duty is < 30 or > 100))
                throw new ArgumentException($"Port {p.Port}: invalid curve.");
            if (p.CustomCurveBackup is null || p.CustomCurveBackup.Count > 20 ||
                (p.CustomCurveBackup.Count > 0 && (p.CustomCurveBackup.Count < 2 || p.CustomCurveBackup.Any(x => !double.IsFinite(x.Temperature) || !double.IsFinite(x.Duty) || x.Temperature is < 0 or > 100 || x.Duty is < 30 or > 100) ||
                 p.CustomCurveBackup.Zip(p.CustomCurveBackup.Skip(1)).Any(x => x.Second.Temperature <= x.First.Temperature))))
                throw new ArgumentException($"Port {p.Port}: invalid saved custom curve.");
            for (int i = 1; i < p.Curve.Count; i++)
                if (p.Curve[i].Temperature <= p.Curve[i - 1].Temperature)
                    throw new ArgumentException($"Port {p.Port}: curve temperatures must be strictly increasing.");
        }
        foreach (var d in new[] { Panel, Aio })
        {
            if (!Enum.IsDefined(d.Content) || d.Brightness is < 0 or > 100 || d.FramesPerSecond is < 1 or > 10 || d.Rotation is not (0 or 90 or 180 or 270))
                throw new ArgumentException("Invalid display setting.");
            if (d.Enabled && d.Content == DisplayContent.Image && !File.Exists(d.MediaPath))
                throw new ArgumentException("Selected image does not exist.");
        }
        WidgetLayout.Validate(Panel, 480, 128);
        WidgetLayout.Validate(Aio, 480, 480);
        if (CoolingEnabled && (!CoolingWiringConfirmed || Ports.All(p => p.Mode == ControlMode.Preserve)))
            throw new ArgumentException("Cooling requires confirmed wiring and at least one configured port.");
        if (StartLiveAtSignIn && !Panel.Enabled && !Aio.Enabled && !CoolingEnabled && !RgbEnabled && !MonitorFanController)
            throw new ArgumentException("Select and validate at least one live device before enabling live sign-in startup.");
    }
    public Settings Copy() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
    public void ValidateLiveSelection()
    {
        Validate();
        if (!Panel.Enabled && !Aio.Enabled && !CoolingEnabled && !RgbEnabled && !MonitorFanController)
            throw new ArgumentException("No live device is selected. Open Displays and enable the correct LCD, or select controller monitoring / RGB / verified cooling ports. Preview alone never opens hardware.");
    }
    public static JsonSerializerOptions JsonOptions => new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}

public static class SettingsStore
{
    public static Settings Load(string path)
    {
        if (!File.Exists(path)) return new();
        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Settings.JsonOptions)
            ?? throw new InvalidDataException("Empty settings file.");
        s.Validate();
        return s;
    }
    public static void Save(string path, Settings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Settings.JsonOptions));
        File.Move(temporary, path, true);
    }
}
