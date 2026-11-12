using System.Globalization;

namespace ThermalDesk.Core;

public enum WidgetSource { Text, CpuTemperature, GpuTemperature, GpuHotSpotTemperature, GpuMemoryTemperature, CpuLoad, GpuLoad, MemoryUsedPercent, Clock, Date, FanRpm, FanDuty, CpuMaximumTemperature,
    GpuVramUsedGiB, GpuVramTotalGiB, GpuVramUsedPercent, CpuClockAverageMhz, CpuClockMaximumMhz, GpuCoreClockMhz, GpuMemoryClockMhz, TopCpuProcess, TopGpuProcess,
    TextFileLog, TopCpuProcessLog, TopGpuProcessLog, GpuSharedMemoryUsedGiB, GpuSharedMemoryTotalGiB,
    CpuPowerWatts, GpuPowerWatts, EstimatedTotalPowerWatts, CpuPowerAverageHour, GpuPowerAverageHour, EstimatedTotalPowerAverageHour,
    CpuEnergyTodayKwh, GpuEnergyTodayKwh, EstimatedTotalEnergyTodayKwh, CpuEnergy24hProjection, GpuEnergy24hProjection, EstimatedTotalEnergy24hProjection,
    CpuEnergyLast24hKwh, GpuEnergyLast24hKwh, EstimatedTotalEnergyLast24hKwh }
public enum WidgetAlignment { Left, Center, Right }
// Append new values: old saved numeric or named profiles retain their original meaning.
public enum WidgetKind { Text, RadialGauge, HorizontalGauge, VerticalGauge, RadialSegments, RadialNeedle, RadialRing, RadialSemicircle, LineGraph, BarGraph }

public sealed class LcdWidget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Widget";
    public WidgetSource Source { get; set; } = WidgetSource.CpuTemperature;
    public WidgetKind Kind { get; set; }
    public double GaugeMinimum { get; set; }
    public double GaugeMaximum { get; set; } = 100;
    public float GaugeThickness { get; set; } = 12;
    public float GaugeStartAngle { get; set; } = 135;
    public float GaugeSweepAngle { get; set; } = 270;
    public string GaugeTrackColor { get; set; } = "243A49";
    public bool GaugeShowText { get; set; } = true;
    public int GaugeSegments { get; set; } = 24;
    public string GaugeTextColor { get; set; } = "";
    public int Port { get; set; } = 1;
    public int X { get; set; } = 20;
    public int Y { get; set; } = 20;
    public int Width { get; set; } = 180;
    public int Height { get; set; } = 50;
    public string FontFamily { get; set; } = "Segoe UI";
    public float FontSize { get; set; } = 28;
    public bool Bold { get; set; } = true;
    public string TextTemplate { get; set; } = "CPU {value} °C";
    public string ValueFormat { get; set; } = "0.0";
    public string Foreground { get; set; } = "FFFFFF";
    public string Background { get; set; } = "";
    public WidgetAlignment Alignment { get; set; } = WidgetAlignment.Center;
    public string LogFilePath { get; set; } = "";
    public int LogLines { get; set; } = 3;
    public LogDisplayMode LogDisplay { get; set; }
    public bool LogAutoFit { get; set; } = true;
    public int LogStaleSeconds { get; set; } = 60;
    public int GraphMinutes { get; set; } = 15;
    public bool GraphAutoScale { get; set; } = true;
    public bool GraphShowLabels { get; set; } = true;
    public float GraphLineWidth { get; set; } = 2;
    public override string ToString() => Name;
}

public static class WidgetLayout
{
    public static string RollingEnergyTemplate(WidgetSource source, int width)
    {
        string label = source == WidgetSource.CpuEnergyLast24hKwh ? "CPU" : source == WidgetSource.GpuEnergyLast24hKwh ? "GPU" : "PC";
        return width < 180 ? label + " 24h {value} kWh\n/{coverage} covered" : label + " 24h {value} kWh /{coverage}";
    }
    // Existing projection widgets become observed rolling totals at profile load.
    // Keep old enum values intact so numeric imports never shift other sources.
    public static void UpgradeEnergyWidgets(Settings settings)
    {
        foreach (var display in new[] { settings.Panel, settings.Aio })
        foreach (var w in display?.Widgets ?? [])
        {
            if (w is null) continue; // Validation below reports malformed profiles.
            WidgetSource? source = w.Source switch { WidgetSource.CpuEnergy24hProjection => WidgetSource.CpuEnergyLast24hKwh,
                WidgetSource.GpuEnergy24hProjection => WidgetSource.GpuEnergyLast24hKwh,
                WidgetSource.EstimatedTotalEnergy24hProjection => WidgetSource.EstimatedTotalEnergyLast24hKwh, _ => null };
            if (source is null) continue;
            w.Source = source.Value;
            w.TextTemplate = RollingEnergyTemplate(w.Source, w.Width);
        }
    }
    public static bool IsGraph(WidgetKind kind) => kind is WidgetKind.LineGraph or WidgetKind.BarGraph;
    public static bool IsGauge(WidgetKind kind) => kind != WidgetKind.Text && !IsGraph(kind);
    public static bool IsNumeric(WidgetSource source) => source is not (WidgetSource.Text or WidgetSource.Clock or WidgetSource.Date or WidgetSource.TopCpuProcess or WidgetSource.TopGpuProcess) && !LogWidget.IsLog(source);
    public static bool IsRadial(WidgetKind kind) => kind is WidgetKind.RadialGauge or WidgetKind.RadialSegments or WidgetKind.RadialNeedle or WidgetKind.RadialRing or WidgetKind.RadialSemicircle;
    public static void Reframe(DisplaySettings display, int oldWidth, int oldHeight, int newWidth, int newHeight)
    {
        foreach (var w in display.Widgets)
        {
            w.Width = Math.Clamp((int)Math.Round(w.Width * (double)newWidth / oldWidth), 8, newWidth);
            w.Height = Math.Clamp((int)Math.Round(w.Height * (double)newHeight / oldHeight), 8, newHeight);
            w.X = Math.Clamp((int)Math.Round(w.X * (double)newWidth / oldWidth), 0, newWidth - w.Width);
            w.Y = Math.Clamp((int)Math.Round(w.Y * (double)newHeight / oldHeight), 0, newHeight - w.Height);
            w.FontSize = Math.Clamp(w.FontSize * Math.Min((float)newWidth / oldWidth, (float)newHeight / oldHeight), 6, 120);
            if (IsRadial(w.Kind)) w.GaugeThickness = Math.Clamp(w.GaugeThickness * Math.Min((float)newWidth / oldWidth, (float)newHeight / oldHeight), 1, Math.Min(60, Math.Min(w.Width, w.Height) / 3f));
        }
    }
    public static (int Width, int Height) Canvas(DisplaySettings settings, int width, int height) => settings.Rotation is 90 or 270 ? (height, width) : (width, height);
    public static bool IsColor(string? color, bool transparent = false) => color is not null &&
        ((transparent && color.Length == 0) || color.TrimStart('#') is { Length: 6 } hex && hex.All(Uri.IsHexDigit));
    public static void Validate(DisplaySettings display, int physicalWidth, int physicalHeight)
    {
        if (!IsColor(display.BackgroundColor) || display.Widgets is null || display.Widgets.Count > 40)
            throw new ArgumentException("Invalid LCD background or more than 40 widgets.");
        var (width, height) = Canvas(display, physicalWidth, physicalHeight);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in display.Widgets)
        {
            if (w is null || string.IsNullOrWhiteSpace(w.Id) || !ids.Add(w.Id) || w.Id.Length > 80 || string.IsNullOrWhiteSpace(w.Name) || w.Name.Length > 80)
                throw new ArgumentException("Widgets need unique IDs and a short name.");
            if (!Enum.IsDefined(w.Source) || !Enum.IsDefined(w.Alignment) || !Enum.IsDefined(w.Kind) || w.Port is < 1 or > 5)
                throw new ArgumentException($"{w.Name}: invalid data source, alignment or fan port.");
            if (w.X < 0 || w.Y < 0 || w.Width < 8 || w.Height < 8 || w.Width > width || w.Height > height || w.X > width - w.Width || w.Y > height - w.Height)
                throw new ArgumentException($"{w.Name}: widget must stay inside the {width}×{height} design canvas (before rotation).");
            if (!float.IsFinite(w.FontSize) || w.FontSize is < 6 or > 120 || string.IsNullOrWhiteSpace(w.FontFamily) || w.FontFamily.Length > 100)
                throw new ArgumentException($"{w.Name}: font must be 6–120 pixels and have a valid family name.");
            if (!IsColor(w.Foreground) || !IsColor(w.Background, true)) throw new ArgumentException($"{w.Name}: use six-digit hex colors, or an empty background for transparency.");
            if (!double.IsFinite(w.GaugeMinimum) || !double.IsFinite(w.GaugeMaximum) || w.GaugeMaximum <= w.GaugeMinimum ||
                !float.IsFinite(w.GaugeThickness) || w.GaugeThickness is < 1 or > 60 ||
                !float.IsFinite(w.GaugeStartAngle) || w.GaugeStartAngle is < -360 or > 360 ||
                !float.IsFinite(w.GaugeSweepAngle) || w.GaugeSweepAngle is < 1 or > 360 || !IsColor(w.GaugeTrackColor) || !IsColor(w.GaugeTextColor, true))
                throw new ArgumentException($"{w.Name}: invalid gauge range, thickness, angle or track color.");
            if (w.GaugeSegments is < 4 or > 80) throw new ArgumentException($"{w.Name}: use 4–80 gauge segments.");
            if ((IsGauge(w.Kind) || IsGraph(w.Kind)) && !IsNumeric(w.Source))
                throw new ArgumentException($"{w.Name}: gauges and graphs need a numeric sensor source.");
            if (w.GraphMinutes is < 1 or > 60 || !float.IsFinite(w.GraphLineWidth) || w.GraphLineWidth is < 1 or > 6)
                throw new ArgumentException($"{w.Name}: graph history must be 1–60 minutes; line width 1–6 px.");
            if (w.LogLines is < 1 or > 10 || w.LogFilePath is null || w.LogFilePath.Length > 500 || w.LogFilePath.Any(char.IsControl))
                throw new ArgumentException($"{w.Name}: log lines must be 1–10; use a short local file path.");
            if (!Enum.IsDefined(w.LogDisplay) || w.LogStaleSeconds is < 15 or > 600) throw new ArgumentException($"{w.Name}: invalid log display mode or stale interval (15–600 seconds).");
            if (w.Source == WidgetSource.TextFileLog && w.LogFilePath.Length > 0 && !BoundedLogTail.IsLocalPath(w.LogFilePath))
                throw new ArgumentException($"{w.Name}: select an absolute local log path, not a network/device path.");
            if (IsRadial(w.Kind) && w.GaugeThickness > Math.Min(w.Width, w.Height) / 3f)
                throw new ArgumentException($"{w.Name}: reduce the radial gauge thickness or enlarge the widget.");
            if (w.TextTemplate is null || w.TextTemplate.Length > 256 || w.ValueFormat is null || w.ValueFormat.Length > 64)
                throw new ArgumentException($"{w.Name}: text/format is too long.");
            try
            {
                if (w.Source is WidgetSource.Clock or WidgetSource.Date) _ = DateTime.Now.ToString(w.ValueFormat, CultureInfo.InvariantCulture);
                else _ = 12.34.ToString(w.ValueFormat, CultureInfo.InvariantCulture);
            }
            catch (FormatException) { throw new ArgumentException($"{w.Name}: invalid value format."); }
        }
    }
    public static string Format(LcdWidget w, Telemetry t)
    {
        if (w.Source == WidgetSource.Text) return w.TextTemplate;
        if (LogWidget.IsLog(w.Source))
        {
            string text = t.LogExcerpts.TryGetValue(LogWidget.Key(w), out var excerpt)
                ? LogPresentation.Format(w, excerpt, t.Time)
                : "Waiting for log";
            return w.TextTemplate.Replace("{value}", text, StringComparison.Ordinal);
        }
        if (w.Source is WidgetSource.TopCpuProcess or WidgetSource.TopGpuProcess)
        {
            var process = w.Source == WidgetSource.TopCpuProcess ? t.TopCpuProcess : t.TopGpuProcess;
            return w.TextTemplate.Replace("{value}", process?.Summary ?? "—", StringComparison.Ordinal);
        }
        double? value = NumericValue(w, t);
        string formatted = w.Source is WidgetSource.Clock or WidgetSource.Date ? t.Time.LocalDateTime.ToString(w.ValueFormat, CultureInfo.InvariantCulture)
            : value.HasValue && double.IsFinite(value.Value) ? value.Value.ToString(w.ValueFormat, CultureInfo.InvariantCulture) : "—";
        string coverage = w.Source switch { WidgetSource.CpuPowerAverageHour => t.CpuPowerHour.Coverage, WidgetSource.GpuPowerAverageHour => t.GpuPowerHour.Coverage, WidgetSource.EstimatedTotalPowerAverageHour => t.TotalPowerHour.Coverage,
            WidgetSource.CpuEnergyTodayKwh => new PowerAverage(null, t.EnergyToday.CpuSeconds).Coverage, WidgetSource.GpuEnergyTodayKwh => new PowerAverage(null, t.EnergyToday.GpuSeconds).Coverage,
            WidgetSource.EstimatedTotalEnergyTodayKwh => new PowerAverage(null, t.EnergyToday.TotalSeconds).Coverage,
            WidgetSource.CpuEnergyLast24hKwh => new PowerAverage(null, t.Energy24h.CpuSeconds).Coverage,
            WidgetSource.GpuEnergyLast24hKwh => new PowerAverage(null, t.Energy24h.GpuSeconds).Coverage,
            WidgetSource.EstimatedTotalEnergyLast24hKwh => new PowerAverage(null, t.Energy24h.TotalSeconds).Coverage, _ => "" };
        return w.TextTemplate.Replace("{value}", formatted, StringComparison.Ordinal).Replace("{coverage}", coverage, StringComparison.Ordinal);
    }
    public static double? NumericValue(LcdWidget w, Telemetry t) => NumericValue(w.Source, t, w.Port);
    public static double? NumericValue(WidgetSource source, Telemetry t, int port = 1) => source switch
        {
            WidgetSource.CpuTemperature => t.CpuTemperature, WidgetSource.GpuTemperature => t.GpuTemperature,
            WidgetSource.CpuMaximumTemperature => t.CpuMaximumTemperature,
            WidgetSource.GpuHotSpotTemperature => t.GpuHotSpotTemperature, WidgetSource.GpuMemoryTemperature => t.GpuMemoryTemperature,
            WidgetSource.CpuLoad => t.CpuLoad, WidgetSource.GpuLoad => t.GpuLoad, WidgetSource.MemoryUsedPercent => t.MemoryUsedPercent,
            WidgetSource.GpuVramUsedGiB => t.GpuVramUsedGiB, WidgetSource.GpuVramTotalGiB => t.GpuVramTotalGiB, WidgetSource.GpuVramUsedPercent => t.GpuVramUsedPercent,
            WidgetSource.GpuSharedMemoryUsedGiB => t.GpuSharedMemoryUsedGiB, WidgetSource.GpuSharedMemoryTotalGiB => t.GpuSharedMemoryTotalGiB,
            WidgetSource.CpuClockAverageMhz => t.CpuClockAverageMhz, WidgetSource.CpuClockMaximumMhz => t.CpuClockMaximumMhz,
            WidgetSource.GpuCoreClockMhz => t.GpuCoreClockMhz, WidgetSource.GpuMemoryClockMhz => t.GpuMemoryClockMhz,
            WidgetSource.CpuPowerWatts => t.CpuPowerWatts, WidgetSource.GpuPowerWatts => t.GpuPowerWatts,
            WidgetSource.EstimatedTotalPowerWatts => t.EstimatedTotalPowerWatts,
            WidgetSource.CpuPowerAverageHour => t.CpuPowerHour.Watts, WidgetSource.GpuPowerAverageHour => t.GpuPowerHour.Watts,
            WidgetSource.EstimatedTotalPowerAverageHour => t.TotalPowerHour.Watts,
            WidgetSource.CpuEnergyTodayKwh => t.EnergyToday.CpuKwh, WidgetSource.GpuEnergyTodayKwh => t.EnergyToday.GpuKwh, WidgetSource.EstimatedTotalEnergyTodayKwh => t.EnergyToday.TotalKwh,
            WidgetSource.CpuEnergy24hProjection => t.CpuPowerHour.Watts * 24 / 1000, WidgetSource.GpuEnergy24hProjection => t.GpuPowerHour.Watts * 24 / 1000,
            WidgetSource.EstimatedTotalEnergy24hProjection => t.TotalPowerHour.Watts * 24 / 1000,
            WidgetSource.CpuEnergyLast24hKwh => t.Energy24h.CpuKwh, WidgetSource.GpuEnergyLast24hKwh => t.Energy24h.GpuKwh,
            WidgetSource.EstimatedTotalEnergyLast24hKwh => t.Energy24h.TotalKwh,
            WidgetSource.FanRpm => t.Fans.FirstOrDefault(f => f.Port == port)?.Rpm,
            WidgetSource.FanDuty => t.Fans.FirstOrDefault(f => f.Port == port)?.Duty, _ => null
        };
    public static double? GaugeFraction(LcdWidget w, Telemetry t)
    {
        var value = NumericValue(w, t);
        return value.HasValue && double.IsFinite(value.Value) && w.GaugeMaximum > w.GaugeMinimum
            ? Math.Clamp((value.Value - w.GaugeMinimum) / (w.GaugeMaximum - w.GaugeMinimum), 0, 1) : null;
    }
    public static List<LcdWidget> Defaults(int width, int height)
    {
        LcdWidget W(string name, WidgetSource source, string text, int x, int y, int w, int h, float font, string color = "FFFFFF", string format = "0") => new()
        { Name = name, Source = source, TextTemplate = text, X = x, Y = y, Width = w, Height = h, FontSize = font, Foreground = color, ValueFormat = format };
        if (width >= height * 2)
            return [W("Title", WidgetSource.Text, "THERMALDESK", 12, 4, width - 24, 22, 16, "26DAB3"),
                W("CPU", WidgetSource.CpuTemperature, "CPU {value} °C", 8, 31, width / 3 - 12, 56, 23),
                W("GPU", WidgetSource.GpuTemperature, "GPU {value} °C", width / 3, 31, width / 3 - 8, 56, 23),
                W("RAM", WidgetSource.MemoryUsedPercent, "RAM {value}%", width * 2 / 3, 31, width / 3 - 8, 56, 23),
                W("Clock", WidgetSource.Clock, "{value}", 12, height - 32, width - 24, 26, 18, "26DAB3", "HH:mm:ss")];
        if (width < 240)
            return [W("Title", WidgetSource.Text, "THERMALDESK", 4, 35, width - 8, 30, 13, "26DAB3"),
                W("CPU", WidgetSource.CpuTemperature, "CPU {value} °C", 4, 100, width - 8, 60, 18),
                W("GPU", WidgetSource.GpuTemperature, "GPU {value} °C", 4, 185, width - 8, 60, 18),
                W("RAM", WidgetSource.MemoryUsedPercent, "RAM {value}%", 4, 270, width - 8, 50, 20),
                W("Clock", WidgetSource.Clock, "{value}", 4, height - 100, width - 8, 36, 18, "26DAB3", "HH:mm:ss")];
        return [W("Title", WidgetSource.Text, "THERMALDESK", 64, 58, 352, 38, 26, "26DAB3"),
            W("CPU label", WidgetSource.Text, "CPU TEMPERATURE", 80, 110, 320, 32, 20, "97ACBF"),
            W("CPU value", WidgetSource.CpuTemperature, "{value} °C", 60, 150, 360, 105, 76),
            W("GPU", WidgetSource.GpuTemperature, "GPU {value} °C", 75, 275, 330, 45, 30, "97ACBF"),
            W("RAM", WidgetSource.MemoryUsedPercent, "MEMORY {value}%", 90, 327, 300, 32, 22, "97ACBF"),
            W("Clock", WidgetSource.Clock, "{value}", 100, 371, 280, 40, 26, "26DAB3", "HH:mm:ss"),
            new LcdWidget { Name = "CPU temperature ring", Kind = WidgetKind.RadialGauge, Source = WidgetSource.CpuTemperature,
                X = 20, Y = 20, Width = 440, Height = 440, Foreground = "26DAB3", GaugeShowText = false }];
    }
}
