using System.Globalization;

namespace ThermalDesk.Core;

public enum WidgetSource { Text, CpuTemperature, GpuTemperature, GpuHotSpotTemperature, GpuMemoryTemperature, CpuLoad, GpuLoad, MemoryUsedPercent, Clock, Date, FanRpm, FanDuty }
public enum WidgetAlignment { Left, Center, Right }

public sealed class LcdWidget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Widget";
    public WidgetSource Source { get; set; } = WidgetSource.CpuTemperature;
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
    public override string ToString() => Name;
}

public static class WidgetLayout
{
    public static void Reframe(DisplaySettings display, int oldWidth, int oldHeight, int newWidth, int newHeight)
    {
        foreach (var w in display.Widgets)
        {
            w.Width = Math.Clamp((int)Math.Round(w.Width * (double)newWidth / oldWidth), 8, newWidth);
            w.Height = Math.Clamp((int)Math.Round(w.Height * (double)newHeight / oldHeight), 8, newHeight);
            w.X = Math.Clamp((int)Math.Round(w.X * (double)newWidth / oldWidth), 0, newWidth - w.Width);
            w.Y = Math.Clamp((int)Math.Round(w.Y * (double)newHeight / oldHeight), 0, newHeight - w.Height);
            w.FontSize = Math.Clamp(w.FontSize * Math.Min((float)newWidth / oldWidth, (float)newHeight / oldHeight), 6, 120);
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
            if (!Enum.IsDefined(w.Source) || !Enum.IsDefined(w.Alignment) || w.Port is < 1 or > 5)
                throw new ArgumentException($"{w.Name}: invalid data source, alignment or fan port.");
            if (w.X < 0 || w.Y < 0 || w.Width < 8 || w.Height < 8 || w.Width > width || w.Height > height || w.X > width - w.Width || w.Y > height - w.Height)
                throw new ArgumentException($"{w.Name}: widget must stay inside the {width}×{height} design canvas (before rotation).");
            if (!float.IsFinite(w.FontSize) || w.FontSize is < 6 or > 120 || string.IsNullOrWhiteSpace(w.FontFamily) || w.FontFamily.Length > 100)
                throw new ArgumentException($"{w.Name}: font must be 6–120 pixels and have a valid family name.");
            if (!IsColor(w.Foreground) || !IsColor(w.Background, true)) throw new ArgumentException($"{w.Name}: use six-digit hex colors, or an empty background for transparency.");
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
        double? value = w.Source switch
        {
            WidgetSource.CpuTemperature => t.CpuTemperature, WidgetSource.GpuTemperature => t.GpuTemperature,
            WidgetSource.GpuHotSpotTemperature => t.GpuHotSpotTemperature, WidgetSource.GpuMemoryTemperature => t.GpuMemoryTemperature,
            WidgetSource.CpuLoad => t.CpuLoad, WidgetSource.GpuLoad => t.GpuLoad, WidgetSource.MemoryUsedPercent => t.MemoryUsedPercent,
            WidgetSource.FanRpm => t.Fans.FirstOrDefault(f => f.Port == w.Port)?.Rpm,
            WidgetSource.FanDuty => t.Fans.FirstOrDefault(f => f.Port == w.Port)?.Duty, _ => null
        };
        string formatted = w.Source is WidgetSource.Clock or WidgetSource.Date ? t.Time.LocalDateTime.ToString(w.ValueFormat, CultureInfo.InvariantCulture)
            : value.HasValue && double.IsFinite(value.Value) ? value.Value.ToString(w.ValueFormat, CultureInfo.InvariantCulture) : "—";
        return w.TextTemplate.Replace("{value}", formatted, StringComparison.Ordinal);
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
                W("CPU", WidgetSource.CpuTemperature, "CPU {value}°", 4, 100, width - 8, 60, 24),
                W("GPU", WidgetSource.GpuTemperature, "GPU {value}°", 4, 185, width - 8, 60, 24),
                W("RAM", WidgetSource.MemoryUsedPercent, "RAM {value}%", 4, 270, width - 8, 50, 20),
                W("Clock", WidgetSource.Clock, "{value}", 4, height - 100, width - 8, 36, 18, "26DAB3", "HH:mm:ss")];
        return [W("Title", WidgetSource.Text, "THERMALDESK", 64, 58, 352, 38, 26, "26DAB3"),
            W("CPU label", WidgetSource.Text, "CPU TEMPERATURE", 80, 110, 320, 32, 20, "97ACBF"),
            W("CPU value", WidgetSource.CpuTemperature, "{value}°", 60, 150, 360, 105, 88),
            W("GPU", WidgetSource.GpuTemperature, "GPU {value} °C", 75, 275, 330, 45, 30, "97ACBF"),
            W("RAM", WidgetSource.MemoryUsedPercent, "MEMORY {value}%", 90, 327, 300, 32, 22, "97ACBF"),
            W("Clock", WidgetSource.Clock, "{value}", 100, 371, 280, 40, 26, "26DAB3", "HH:mm:ss")];
    }
}
