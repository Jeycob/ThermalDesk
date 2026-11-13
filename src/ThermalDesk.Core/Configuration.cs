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
    public double? CpuMaximumTemperature { get; init; }
    public double? GpuVramUsedGiB { get; init; }
    public double? GpuVramTotalGiB { get; init; }
    public double? GpuVramUsedPercent { get; init; }
    public double? GpuSharedMemoryUsedGiB { get; init; }
    public double? GpuSharedMemoryTotalGiB { get; init; }
    public GpuAdapterKind GpuAdapterKind { get; init; }
    public string GpuMemorySource { get; init; } = "";
    public string GpuLoadSource { get; init; } = "";
    public string CpuDeviceName { get; init; } = "";
    public string CpuLoadSource { get; init; } = "";
    public double? CpuClockAverageMhz { get; init; }
    public double? CpuClockMaximumMhz { get; init; }
    public double? GpuCoreClockMhz { get; init; }
    public double? GpuMemoryClockMhz { get; init; }
    public double? CpuPowerWatts { get; init; }
    public double? GpuPowerWatts { get; init; }
    public double? EstimatedTotalPowerWatts { get; init; }
    public string CpuPowerSource { get; init; } = "";
    public string GpuPowerSource { get; init; } = "";
    public string PowerEstimateStatus { get; init; } = "";
    public PowerAverage CpuPowerHour { get; init; } = PowerAverage.Empty;
    public PowerAverage GpuPowerHour { get; init; } = PowerAverage.Empty;
    public PowerAverage TotalPowerHour { get; init; } = PowerAverage.Empty;
    public DailyEnergyState EnergyToday { get; init; } = DailyEnergyState.Empty;
    [JsonIgnore] public RollingEnergyState Energy24h { get; init; } = RollingEnergyState.Empty;
    [JsonIgnore] public HistorySnapshot History { get; init; } = HistorySnapshot.Empty;
    public ProcessUse? TopCpuProcess { get; init; }
    public ProcessUse? TopGpuProcess { get; init; }
    public DateTimeOffset ProcessSampleTime { get; init; }
    public string ProcessMonitorStatus { get; init; } = "Process monitoring disabled";
    [JsonIgnore] public IReadOnlyDictionary<string, LogExcerpt> LogExcerpts { get; init; } = LogExcerpt.Empty;
    public IReadOnlyList<FanReading> Fans { get; init; } = [];
    public string CpuTemperatureSource { get; init; } = "";
    public string CpuMaximumTemperatureSource { get; init; } = "";
    public string GpuTemperatureSource { get; init; } = "";
    public string MemoryLoadSource { get; init; } = "";
    public string GpuDeviceId { get; init; } = "";
    public string GpuDeviceName { get; init; } = "";
    public IReadOnlyList<GpuDeviceInfo> GpuDevices { get; init; } = [];
}
public sealed record FanReading(int Port, int Rpm, int Duty);
public sealed record GpuDeviceInfo(string Id, string Name) { public GpuAdapterKind Kind { get; init; } }

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
    // Old profiles with Rgb=false mean "leave lighting alone". Only explicit user
    // OFF requests take ownership of a dark output, including after restarting.
    public bool RgbOffRequested { get; set; }
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
    public bool RotateWidgetGroups { get; set; }
    public int WidgetGroupSeconds { get; set; } = 5;
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
    public bool AllowMonitoringOnly { get; set; }
    public string PreferredGpuId { get; set; } = "";
    public bool StartLiveAtSignIn { get; set; }
    // Older explicitly armed sign-in profiles may resume on manual launch too.
    // New/unarmed profiles remain Preview until the user opts in.
    public bool? ResumeLastProfileOnLaunch { get; set; }
    public bool LastSessionWasLive { get; set; }
    public bool ShowTechnicalDetails { get; set; }
    // Null preserves --minimized behavior for profiles from older releases.
    // Once the user selects either state, that explicit choice wins over old task arguments.
    public bool? MinimizeAfterStartup { get; set; }
    public bool MinimizeWhenClosed { get; set; }
    public bool ProcessMonitorEnabled { get; set; }
    public int ProcessSampleSeconds { get; set; } = 5;
    public int LogSampleSeconds { get; set; } = 5;
    public double PowerEstimateExtraWatts { get; set; } = 40;
    public double PowerEstimateEfficiencyPercent { get; set; } = 90;
    public bool PowerEstimateIncludesGpu { get; set; } = true;
    public string RgbColor { get; set; } = "23D6BA";
    public bool RgbScheduleEnabled { get; set; }
    public List<RgbScheduleEntry> RgbScheduleEntries { get; set; } = [];
    public string ControllerSerial { get; set; } = "";
    public DisplaySettings Panel { get; set; } = new();
    public DisplaySettings Aio { get; set; } = new();
    public List<PortSettings> Ports { get; set; } = Enumerable.Range(1, 5).Select(i => new PortSettings { Port = i }).ToList();
    [JsonIgnore]
    public bool HasUsbSelection => Panel.Enabled || Aio.Enabled || CoolingEnabled || MonitorFanController ||
        RgbEnabled && Ports.Any(Lighting.IsManaged);
    [JsonIgnore] public bool HasLiveSelection => AllowMonitoringOnly || HasUsbSelection;

    public void Validate()
    {
        if (SchemaVersion != 1) throw new ArgumentException("Unsupported configuration version.");
        if (Ports is null || Ports.Any(p => p is null) || Panel is null || Aio is null || RgbColor is null || ControllerSerial is null)
            throw new ArgumentException("Profile contains a null required object or field.");
        if (ControllerSerial.Length > 200 || ControllerSerial.Any(char.IsControl)) throw new ArgumentException("Controller serial must be at most 200 printable characters.");
        if (PollSeconds is < 1 or > 10) throw new ArgumentException("Polling must be 1–10 seconds.");
        if (ProcessSampleSeconds is < 5 or > 60) throw new ArgumentException("Process sampling must be 5–60 seconds.");
        if (LogSampleSeconds is < 5 or > 60) throw new ArgumentException("Log sampling must be 5–60 seconds.");
        if (!double.IsFinite(PowerEstimateExtraWatts) || PowerEstimateExtraWatts is < 0 or > 500 ||
            !double.IsFinite(PowerEstimateEfficiencyPercent) || PowerEstimateEfficiencyPercent is < 50 or > 100)
            throw new ArgumentException("Power estimate: extra system load must be 0–500 W; PSU efficiency 50–100%.");
        if (PreferredGpuId is null || PreferredGpuId.Length > 200 || PreferredGpuId.Any(char.IsControl)) throw new ArgumentException("GPU identifier must be at most 200 printable characters.");
        if (Ports.Count != 5 || !Ports.Select(p => p.Port).Order().SequenceEqual(Enumerable.Range(1, 5)))
            throw new ArgumentException("Exactly five distinct controller ports (1–5) are required.");
        if (RgbColor.Length != 6 || !RgbColor.All(Uri.IsHexDigit)) throw new ArgumentException("RGB color must be six hexadecimal digits.");
        RgbSchedule.Validate(this);
        if (RgbEnabled && !Ports.Any(Lighting.IsManaged))
            throw new ArgumentException("Select at least one RGB port for ON or explicit OFF, or disable the global RGB master.");
        foreach (var p in Ports)
        {
            if (p.Curve is null || p.Curve.Any(x => x is null) || p.CustomCurveBackup is null || p.CustomCurveBackup.Any(x => x is null))
                throw new ArgumentException($"Port {p.Port}: curve data must not be null.");
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
            if (d.MediaPath is null) throw new ArgumentException("Display media path must not be null.");
            if (!Enum.IsDefined(d.Content) || d.Brightness is < 0 or > 100 || d.FramesPerSecond is < 1 or > 10 || d.Rotation is not (0 or 90 or 180 or 270))
                throw new ArgumentException("Invalid display setting.");
            if (d.Enabled && d.Content == DisplayContent.Image && !File.Exists(d.MediaPath))
                throw new ArgumentException("Selected image does not exist.");
        }
        WidgetLayout.Validate(Panel, 480, 128);
        WidgetLayout.Validate(Aio, 480, 480);
        if (OnlineMediaPolicy.Sources(this).Select(OnlineMediaPolicy.Key).Distinct().Count() > OnlineMediaPolicy.MaxSources)
            throw new ArgumentException("At most four different online sources can be active across both LCDs.");
        if (CoolingEnabled && (!CoolingWiringConfirmed || Ports.All(p => p.Mode == ControlMode.Preserve)))
            throw new ArgumentException("Cooling requires confirmed wiring and at least one configured port.");
        // The saved sign-in preference is independent of the current output
        // selection. Turning the last output off must remain saveable; startup
        // falls back to Preview until a live device is selected again.
    }
    public Settings Copy() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
    public void ValidateLiveSelection()
    {
        Validate();
        if (!HasLiveSelection)
            throw new ArgumentException("No Live mode is selected. Enable monitoring-only in Sensors, or select an LCD / controller monitoring / RGB / verified cooling port. Preview never opens hardware.");
    }
    // Reuse serializer metadata instead of rebuilding it on every copy/save/preview.
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}

public sealed record StartupProfile(Settings Settings, bool Recovered, string Warning, string? PreservedPath);

public static class SettingsStore
{
    public const int MaximumProfileBytes = 1024 * 1024;
    public static Settings LoadForImport(string path, bool keepLiveAtSignIn, bool? keepResumeOnLaunch = null)
    {
        // Unlike first launch, a missing or unrelated file must never become defaults.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumProfileBytes) throw new InvalidDataException("Profile exceeds the 1 MiB limit.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !new[] { "SchemaVersion", "Panel", "Aio", "Ports" }.All(name => root.TryGetProperty(name, out _)))
            throw new InvalidDataException("This is not a ThermalDesk profile. Select an exported profile, settings.json, or settings.json.previous, not a diagnostic report.");
        var result = root.Deserialize<Settings>(Settings.JsonOptions) ?? throw new InvalidDataException("Empty profile.");
        WidgetLayout.UpgradeEnergyWidgets(result);
        result.Validate();
        // A transferred profile cannot silently enable or disable this PC's startup preference.
        result.StartLiveAtSignIn = keepLiveAtSignIn;
        result.ResumeLastProfileOnLaunch = keepResumeOnLaunch;
        result.LastSessionWasLive = false; // Import is never a new live-start authorization.
        return result;
    }
    public static string BackupBeforeImport(string path, Settings current)
    {
        string backup = path + ".before-import-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json";
        if (File.Exists(path)) File.Copy(path, backup, overwrite: false);
        else Save(backup, current);
        return backup;
    }
    public static Settings Load(string path)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        using (stream)
        {
            if (stream.Length > MaximumProfileBytes) throw new InvalidDataException("Settings exceed the 1 MiB profile limit. Move the original aside before saving a replacement.");
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var s = JsonSerializer.Deserialize<Settings>(reader.ReadToEnd(), Settings.JsonOptions) ?? throw new InvalidDataException("Empty settings file.");
            WidgetLayout.UpgradeEnergyWidgets(s);
            s.Validate(); return s;
        }
    }
    private static string PreserveInvalid(string path)
    {
        if (new FileInfo(path).Length > MaximumProfileBytes) throw new InvalidDataException("Oversized original left untouched; move it aside before saving a replacement.");
        string preserved = path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json";
        File.Copy(path, preserved, false); return preserved;
    }
    public static StartupProfile LoadForStartup(string path)
    {
        try { return new(Load(path), false, "", null); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            string? preserved = null;
            try { if (File.Exists(path)) preserved = PreserveInvalid(path); }
            catch (Exception copyError) when (copyError is InvalidDataException or IOException or UnauthorizedAccessException) { }
            var recovered = new Settings(); bool previous = false;
            try { if (File.Exists(path + ".previous")) { recovered = Load(path + ".previous"); previous = true; } }
            catch (Exception backupError) when (backupError is JsonException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException) { }
            // A valid old backup is useful for editing, never authority to rearm hardware.
            recovered.StartLiveAtSignIn = recovered.CoolingEnabled = recovered.RgbEnabled = recovered.MonitorFanController = recovered.CoolingWiringConfirmed = false;
            recovered.ResumeLastProfileOnLaunch = false;
            recovered.Aio.Enabled = recovered.Panel.Enabled = false;
            string warning = "Saved profile could not be loaded (" + error.GetType().Name + "). " +
                (previous ? "Previous profile opened in Preview only. " : "Defaults opened in Preview only. ") +
                "All live controls are disabled. " + (preserved is null ? "Original file left untouched; it must be preserved before a replacement can be saved." : "Original copy: " + preserved);
            return new(recovered, true, warning, preserved);
        }
    }
    public static void Save(string path, Settings settings)
    {
        settings.Validate();
        string json = JsonSerializer.Serialize(settings, Settings.JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumProfileBytes) throw new InvalidDataException("Profile exceeds the 1 MiB limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bool exists;
        try { _ = File.GetAttributes(path); exists = true; }
        catch (FileNotFoundException) { exists = false; }
        catch (DirectoryNotFoundException) { exists = false; }
        if (exists)
        {
            bool valid = false;
            try { Load(path); valid = true; }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException) { PreserveInvalid(path); }
            if (valid) File.Copy(path, path + ".previous", true);
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true; stream.Write(System.Text.Encoding.UTF8.GetBytes(json)); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }
}
