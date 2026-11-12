namespace ThermalDesk.Core;

public sealed record LcdPreset(string Id, string Name, bool Portrait = false)
{
    public override string ToString() => Name;
}

/// <summary>Editable widget templates, not hardware commands or saved cooling profiles.</summary>
public static class LcdPresets
{
    private static readonly IReadOnlyList<LcdPreset> Panel = Array.AsReadOnly(new[]
    {
        new LcdPreset("wide-overview", "Landscape · Overview"),
        new LcdPreset("wide-rings", "Landscape · Three gauges"),
        new LcdPreset("wide-focus", "Landscape · Large CPU"),
        new LcdPreset("tall-overview", "Portrait · Overview", true),
        new LcdPreset("tall-rings", "Portrait · Stacked gauges", true),
        new LcdPreset("tall-clock", "Portrait · Clock + temperatures", true),
        new LcdPreset("wide-ai-log", "Landscape · AI log / tok-s"),
        new LcdPreset("tall-ai-log", "Portrait · AI log / tok-s", true),
        new LcdPreset("wide-game-log", "Landscape · Gaming log / FPS"),
        new LcdPreset("tall-game-log", "Portrait · Gaming log / FPS", true),
        new LcdPreset("wide-power", "Landscape · Power + graphs"),
        new LcdPreset("tall-power", "Portrait · Power + graphs", true)
    });
    private static readonly IReadOnlyList<LcdPreset> Round = Array.AsReadOnly(new[]
    {
        new LcdPreset("round-thermal", "Round · CPU + GPU rings"),
        new LcdPreset("round-system", "Round · System overview"),
        new LcdPreset("round-clock", "Round · Large clock"),
        new LcdPreset("round-ai-log", "Round · AI log / tok-s"),
        new LcdPreset("round-game-log", "Round · Gaming log / FPS"),
        new LcdPreset("round-power", "Round · Power + graphs")
    });
    public static IReadOnlyList<LcdPreset> Available(int width, int height) => (width, height) switch
    {
        (480, 128) => Panel,
        (480, 480) => Round,
        _ => throw new ArgumentException("Presets are available for the 480×128 panel and 480×480 round LCD.")
    };

    public static DisplaySettings Create(DisplaySettings current, string id, int width, int height)
    {
        if (current.Rotation is not (0 or 90 or 180 or 270)) throw new ArgumentException("Invalid display rotation.", nameof(current));
        var preset = Available(width, height).SingleOrDefault(p => p.Id == id)
            ?? throw new ArgumentException("Unknown layout preset for this display.", nameof(id));
        var result = current.Copy(); // Never modify a live profile just to preview a preset.
        if (width != height)
        {
            bool wasPortrait = current.Rotation is 90 or 270;
            result.Rotation = wasPortrait == preset.Portrait ? current.Rotation : preset.Portrait ? 90 : 0;
        }
        result.Content = DisplayContent.Dashboard;
        result.UseCustomLayout = result.Overlay = true;
        result.BackgroundColor = "0A111D";
        result.Widgets = Build(id);
        WidgetLayout.Validate(result, width, height);
        return result;
    }

    private const string Cyan = "26DAB3", Purple = "B794FF", Gold = "FFC35B", Muted = "97ACBF";
    private static LcdWidget Text(string name, WidgetSource source, string template, int x, int y, int w, int h,
        float font, string color = "FFFFFF", string format = "0") => new()
    {
        Name = name, Source = source, TextTemplate = template, X = x, Y = y, Width = w, Height = h,
        FontSize = font, Foreground = color, ValueFormat = format
    };
    private static LcdWidget Label(string text, int x, int y, int w, int h = 20, float font = 13, string color = Muted)
        => Text(text, WidgetSource.Text, text, x, y, w, h, font, color);
    private static LcdWidget Gauge(string name, WidgetSource source, int x, int y, int size, string color,
        string template = "{value}%", float font = 20, bool showText = true, float thickness = 8) => new()
    {
        Name = name, Kind = WidgetKind.RadialGauge, Source = source, X = x, Y = y, Width = size, Height = size,
        Foreground = color, TextTemplate = template, FontSize = font, ValueFormat = "0",
        GaugeThickness = thickness, GaugeShowText = showText
    };
    private static List<LcdWidget> Build(string id)
    {
        List<LcdWidget> widgets = [];
        if (id.EndsWith("-power", StringComparison.Ordinal))
        {
            bool portrait = id.StartsWith("tall", StringComparison.Ordinal), round = id.StartsWith("round", StringComparison.Ordinal);
            var sources = new[] { WidgetSource.CpuPowerWatts, WidgetSource.GpuPowerWatts, WidgetSource.EstimatedTotalPowerWatts };
            var averages = new[] { WidgetSource.CpuPowerAverageHour, WidgetSource.GpuPowerAverageHour, WidgetSource.EstimatedTotalPowerAverageHour };
            var names = new[] { "CPU", "GPU", "PC EST" }; var colors = new[] { Cyan, Purple, Gold };
            if (portrait || round) widgets.Add(Label("POWER · W", portrait ? 8 : 100, portrait ? 10 : 68, portrait ? 112 : 280, 25, portrait ? 12 : 18, Cyan));
            for (int i = 0; i < 3; i++)
            {
                int x = portrait ? 8 : round ? 100 : 8 + i * 158;
                int y = portrait ? 44 + i * 140 : round ? 108 + i * 92 : 3;
                int width = portrait ? 112 : round ? 280 : 148;
                widgets.Add(Text(names[i] + " power", sources[i], names[i] + " {value} W", x, y, width, 24, portrait ? 12 : 15, colors[i]));
                widgets.Add(Text(names[i] + " average", averages[i], "AVG {value} W /{coverage}", x, y + 24, width, 20, portrait ? 9 : 10, Muted));
                widgets.Add(new LcdWidget { Name = names[i] + " history", Kind = WidgetKind.LineGraph, Source = sources[i], X = x, Y = y + 46, Width = width, Height = portrait ? 74 : round ? 42 : 55,
                    Foreground = colors[i], TextTemplate = names[i] + " {value} W", FontSize = 10, GraphMinutes = 15, GraphShowLabels = false });
            }
            if (!portrait && !round) widgets.Add(Label("15 min → now · AVG: last-hour available coverage", 8, 107, 464, 19, 10, Muted));
            else widgets.Add(Label("15 min → now", portrait ? 8 : 120, portrait ? 450 : 396, portrait ? 112 : 240, 20, 10, Muted));
            return widgets;
        }
        if (id.EndsWith("-log", StringComparison.Ordinal))
        {
            bool portrait = id.StartsWith("tall-", StringComparison.Ordinal), round = id.StartsWith("round-", StringComparison.Ordinal), game = id.Contains("game", StringComparison.Ordinal);
            string heading = portrait ? (game ? "GAME\nLAST REPORTED" : "AI\nLAST REPORTED") : (game ? "GAME · LAST REPORTED" : "AI · LAST REPORTED");
            widgets.Add(Label(heading, portrait ? 8 : round ? 90 : 12, portrait ? 70 : round ? 90 : 3, portrait ? 112 : round ? 300 : 456, portrait ? 36 : 24, portrait ? 10 : 14, Cyan));
            widgets.Add(new LcdWidget
            {
                Name = game ? "Game log HUD" : "AI throughput", Source = WidgetSource.TopGpuProcessLog,
                LogDisplay = game ? LogDisplayMode.Gaming : LogDisplayMode.AiStats, LogAutoFit = true, LogLines = portrait ? 6 : 3,
                TextTemplate = "{value}", FontFamily = "Consolas", FontSize = portrait ? 18 : round ? 26 : 24, Bold = true,
                X = portrait ? 12 : round ? 80 : 12, Y = portrait ? 125 : round ? 155 : 29,
                Width = portrait ? 104 : round ? 320 : 456, Height = portrait ? 220 : round ? 180 : 92,
                Alignment = WidgetAlignment.Left, Foreground = "FFFFFF"
            });
            return widgets;
        }
        if (id == "wide-overview")
        {
            widgets.Add(Label("THERMALDESK", 12, 3, 190, 22, 13, Cyan));
            widgets.Add(Text("Clock", WidgetSource.Clock, "{value}", 326, 3, 142, 22, 16, Muted, "HH:mm:ss"));
            var sources = new[] { WidgetSource.CpuTemperature, WidgetSource.GpuTemperature, WidgetSource.MemoryUsedPercent };
            var loads = new[] { WidgetSource.CpuLoad, WidgetSource.GpuLoad, WidgetSource.MemoryUsedPercent };
            var names = new[] { "CPU", "GPU", "MEMORY" }; var colors = new[] { Cyan, Purple, Gold };
            for (int i = 0; i < 3; i++)
            {
                int x = 10 + i * 158;
                widgets.Add(Label(names[i], x, 29, 144, 20, 13, colors[i]));
                widgets.Add(Text(names[i] + " value", sources[i], i == 2 ? "{value}%" : "{value} °C", x, 50, 144, 46, 32));
                widgets.Add(Text(names[i] + " detail", loads[i], i == 2 ? "PHYSICAL USED" : "LOAD {value}%", x, 101, 144, 20, 12, Muted));
            }
        }
        else if (id == "wide-rings")
        {
            widgets.Add(Label("CPU °C", 20, 1, 88, 20, 12, Cyan));
            widgets.Add(Label("GPU °C", 196, 1, 88, 20, 12, Purple));
            widgets.Add(Label("MEMORY %", 372, 1, 88, 20, 12, Gold));
            widgets.Add(Gauge("CPU", WidgetSource.CpuTemperature, 20, 23, 88, Cyan, "{value} °C", 18));
            widgets.Add(Gauge("GPU", WidgetSource.GpuTemperature, 196, 23, 88, Purple, "{value} °C", 18));
            widgets.Add(Gauge("Memory", WidgetSource.MemoryUsedPercent, 372, 23, 88, Gold, font: 22));
            widgets.Add(Text("Clock", WidgetSource.Clock, "{value}", 156, 110, 168, 18, 12, Muted, "HH:mm:ss"));
        }
        else if (id == "wide-focus")
        {
            widgets.Add(Label("CPU TEMPERATURE", 12, 4, 242, 22, 14, Cyan));
            widgets.Add(Text("CPU", WidgetSource.CpuTemperature, "{value} °C", 12, 26, 242, 78, 58));
            widgets.Add(Text("CPU load", WidgetSource.CpuLoad, "LOAD {value}%", 12, 103, 242, 20, 13, Muted));
            widgets.Add(Text("GPU", WidgetSource.GpuTemperature, "GPU {value} °C", 274, 12, 194, 42, 25, Purple));
            widgets.Add(Text("Memory", WidgetSource.MemoryUsedPercent, "RAM {value}%", 274, 57, 194, 27, 20, Gold));
            widgets.Add(Text("Clock", WidgetSource.Clock, "{value}", 274, 93, 194, 30, 22, Muted, "HH:mm:ss"));
        }
        else if (id == "tall-overview")
        {
            widgets.Add(Label("THERMALDESK", 6, 12, 116, 24, 13, Cyan));
            widgets.Add(Text("Clock", WidgetSource.Clock, "{value}", 6, 44, 116, 40, 28, "FFFFFF", "HH:mm"));
            var sources = new[] { WidgetSource.CpuTemperature, WidgetSource.GpuTemperature, WidgetSource.MemoryUsedPercent };
            var loads = new[] { WidgetSource.CpuLoad, WidgetSource.GpuLoad, WidgetSource.MemoryUsedPercent };
            var names = new[] { "CPU", "GPU", "MEMORY" }; var colors = new[] { Cyan, Purple, Gold };
            for (int i = 0; i < 3; i++)
            {
                int y = 106 + i * 111;
                widgets.Add(Label(names[i], 6, y, 116, 22, 14, colors[i]));
                widgets.Add(Text(names[i] + " value", sources[i], i == 2 ? "{value}%" : "{value} °C", 6, y + 23, 116, 47, 30));
                widgets.Add(Text(names[i] + " detail", loads[i], i == 2 ? "PHYSICAL USED" : "LOAD {value}%", 6, y + 73, 116, 23, 12, Muted));
            }
            widgets.Add(Text("Date", WidgetSource.Date, "{value}", 6, 447, 116, 22, 13, Muted, "dd MMM yyyy"));
        }
        else if (id == "tall-rings")
        {
            widgets.Add(Label("CPU °C", 6, 10, 116, 22, 14, Cyan));
            widgets.Add(Gauge("CPU", WidgetSource.CpuTemperature, 16, 35, 96, Cyan, "{value} °C", 19));
            widgets.Add(Label("GPU °C", 6, 149, 116, 22, 14, Purple));
            widgets.Add(Gauge("GPU", WidgetSource.GpuTemperature, 16, 174, 96, Purple, "{value} °C", 19));
            widgets.Add(Label("MEMORY %", 6, 288, 116, 22, 14, Gold));
            widgets.Add(Gauge("Memory", WidgetSource.MemoryUsedPercent, 16, 313, 96, Gold, font: 25));
            widgets.Add(Text("Clock", WidgetSource.Clock, "{value}", 6, 437, 116, 30, 20, Muted, "HH:mm:ss"));
        }
        else if (id == "tall-clock")
        {
            widgets.Add(Label("THERMALDESK", 6, 30, 116, 24, 13, Cyan));
            widgets.Add(Text("Hours", WidgetSource.Clock, "{value}", 6, 84, 116, 80, 58, "FFFFFF", "HH"));
            widgets.Add(Text("Minutes", WidgetSource.Clock, "{value}", 6, 165, 116, 80, 58, Cyan, "mm"));
            widgets.Add(Text("Date", WidgetSource.Date, "{value}", 6, 255, 116, 24, 16, Muted, "dd MMM"));
            widgets.Add(Label("CPU", 6, 301, 116, 22, 14, Cyan));
            widgets.Add(Text("CPU", WidgetSource.CpuTemperature, "{value} °C", 6, 324, 116, 46, 30));
            widgets.Add(Label("GPU", 6, 393, 116, 22, 14, Purple));
            widgets.Add(Text("GPU", WidgetSource.GpuTemperature, "{value} °C", 6, 416, 116, 46, 30));
        }
        else if (id == "round-thermal")
        {
            widgets.Add(Gauge("CPU ring", WidgetSource.CpuTemperature, 20, 20, 440, Cyan, showText: false, thickness: 14));
            widgets.Add(Gauge("GPU ring", WidgetSource.GpuTemperature, 63, 63, 354, Purple, showText: false, thickness: 10));
            widgets.Add(Label("CPU", 150, 112, 180, 30, 21, Cyan));
            widgets.Add(Text("CPU", WidgetSource.CpuTemperature, "{value} °C", 100, 144, 280, 94, 62));
            widgets.Add(Text("GPU", WidgetSource.GpuTemperature, "GPU {value} °C", 120, 245, 240, 45, 30, Purple));
            widgets.Add(Text("Clock", WidgetSource.Clock, "{value}", 140, 315, 200, 36, 25, Muted, "HH:mm:ss"));
        }
        else if (id == "round-system")
        {
            widgets.Add(Gauge("CPU ring", WidgetSource.CpuTemperature, 20, 20, 440, Cyan, showText: false, thickness: 8));
            widgets.Add(Label("SYSTEM OVERVIEW", 100, 68, 280, 34, 23, Cyan));
            widgets.Add(Label("CPU °C", 70, 125, 160)); widgets.Add(Label("GPU °C", 250, 125, 160, color: Purple));
            widgets.Add(Text("CPU", WidgetSource.CpuTemperature, "{value}", 70, 151, 160, 72, 52));
            widgets.Add(Text("GPU", WidgetSource.GpuTemperature, "{value}", 250, 151, 160, 72, 52));
            widgets.Add(Label("CPU LOAD", 70, 245, 160)); widgets.Add(Label("RAM USED", 250, 245, 160, color: Gold));
            widgets.Add(Text("CPU load", WidgetSource.CpuLoad, "{value}%", 70, 268, 160, 58, 40));
            widgets.Add(Text("Memory", WidgetSource.MemoryUsedPercent, "{value}%", 250, 268, 160, 58, 40));
            widgets.Add(Text("Clock", WidgetSource.Clock, "{value}", 100, 365, 280, 36, 26, Cyan, "HH:mm:ss"));
        }
        else if (id == "round-clock")
        {
            widgets.Add(Gauge("Memory ring", WidgetSource.MemoryUsedPercent, 20, 20, 440, Gold, showText: false, thickness: 8));
            widgets.Add(Text("Date", WidgetSource.Date, "{value}", 100, 106, 280, 36, 25, Muted, "ddd dd MMM"));
            widgets.Add(Text("Clock", WidgetSource.Clock, "{value}", 60, 145, 360, 145, 94, "FFFFFF", "HH:mm"));
            widgets.Add(Text("CPU", WidgetSource.CpuTemperature, "CPU {value} °C", 100, 299, 280, 40, 28, Cyan));
            widgets.Add(Text("GPU", WidgetSource.GpuTemperature, "GPU {value} °C", 100, 346, 280, 40, 28, Purple));
            widgets.Add(Text("Memory ring legend", WidgetSource.MemoryUsedPercent, "RAM {value}%", 130, 400, 220, 24, 14, Gold));
        }
        else throw new ArgumentException("Unknown layout preset.", nameof(id));
        return widgets;
    }
}
