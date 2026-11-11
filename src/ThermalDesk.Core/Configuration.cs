using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThermalDesk.Core;

public enum ControlMode { Preserve, Fixed, Curve }
public enum PortRole { Unknown, Fan, Pump }
public enum TemperatureSource { Cpu, Gpu, Maximum }
public enum DisplayContent { Dashboard, Image, Screen }
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
    public PortRole Role { get; set; } = PortRole.Unknown;
    public ControlMode Mode { get; set; } = ControlMode.Preserve;
    public TemperatureSource Source { get; set; } = TemperatureSource.Maximum;
    public int MinimumDuty { get; set; } = 35;
    public int FixedDuty { get; set; } = 70;
    public bool Rgb { get; set; }
    public int LedCount { get; set; } = 12;
    public List<CurvePoint> Curve { get; set; } = [new(30, 40), new(50, 50), new(65, 70), new(75, 90), new(80, 100)];
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
            if (!Enum.IsDefined(p.Role) || !Enum.IsDefined(p.Mode) || !Enum.IsDefined(p.Source)) throw new ArgumentException("Invalid port setting.");
            if (p.MinimumDuty is < 30 or > 100 || p.FixedDuty is < 30 or > 100 || p.LedCount is < 1 or > 62)
                throw new ArgumentException($"Port {p.Port}: duty must be 30–100%, LEDs 1–62.");
            if (p.Mode != ControlMode.Preserve && p.Role == PortRole.Unknown)
                throw new ArgumentException($"Identify fan/pump wiring on port {p.Port} before controlling it.");
            if (p.Curve.Count is < 2 or > 20 || p.Curve.Any(x => !double.IsFinite(x.Temperature) || !double.IsFinite(x.Duty) || x.Temperature is < 0 or > 100 || x.Duty is < 30 or > 100))
                throw new ArgumentException($"Port {p.Port}: invalid curve.");
            for (int i = 1; i < p.Curve.Count; i++)
                if (p.Curve[i].Temperature <= p.Curve[i - 1].Temperature || p.Curve[i].Duty < p.Curve[i - 1].Duty)
                    throw new ArgumentException($"Port {p.Port}: curve must increase in temperature and not decrease in duty.");
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
    }
    public Settings Copy() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
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
