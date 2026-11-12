using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class PowerGraphChecks
{
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    internal static int Run(string directory)
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Power/graph UI: " + message); }
        var start = DateTimeOffset.UtcNow.AddHours(-1); var settings = new Settings(); var history = new TelemetryHistory();
        Telemetry telemetry = new(start, 50, 40, 20, 30, 40);
        for (int i = 0; i <= 720; i++)
            telemetry = history.Add(telemetry with { Time = start.AddSeconds(i * 5), CpuPowerWatts = 80 + 40 * Math.Sin(i / 18d), GpuPowerWatts = 150 + 100 * Math.Cos(i / 28d) }, settings);
        using var renderer = new FrameRenderer();
        foreach (var id in new[] { "wide-power", "tall-power", "round-power" })
        {
            bool round = id.StartsWith("round", StringComparison.Ordinal); var preset = LcdPresets.Create(new(), id, 480, round ? 480 : 128);
            using var image = renderer.Render(telemetry, preset, 480, round ? 480 : 128);
            if (preset.Rotation is 90 or 270) image.RotateFlip(RotateFlipType.Rotate270FlipNone);
            image.Save(Path.Combine(directory, id + "-populated.png"), ImageFormat.Png);
            Check(preset.Widgets.Count(w => WidgetLayout.IsGraph(w.Kind)) == 3, "template has three independent histories: " + id);
            Check(preset.Widgets.Any(w => w.Source == WidgetSource.EstimatedTotalPowerWatts && w.TextTemplate.Contains("EST")), "whole PC stays explicitly estimated: " + id);
        }
        foreach (var kind in new[] { WidgetKind.LineGraph, WidgetKind.BarGraph })
        {
            var widget = new LcdWidget { Kind = kind, Source = WidgetSource.EstimatedTotalPowerWatts, TextTemplate = "PC EST {value} W", X = 8, Y = 8, Width = 464, Height = 112, FontSize = 11, Foreground = "26DAB3", GraphMinutes = 60 };
            var panel = new DisplaySettings { UseCustomLayout = true, Widgets = [widget] };
            WidgetLayout.Validate(panel, 480, 128);
            using var frame = renderer.Render(telemetry, panel, 480, 128);
            frame.Save(Path.Combine(directory, "graph-" + kind + ".png"), ImageFormat.Png);
            Check(frame.Width == 480 && frame.Height == 128, "graph renders at native frame size: " + kind);
            Check(FrameRenderer.VisualKey(telemetry, panel) != FrameRenderer.VisualKey(telemetry with { History = telemetry.History with { Revision = telemetry.History.Revision + 1 } }, panel), "new graph sample invalidates JPEG cache: " + kind);
            var properties = new WidgetProperties(widget); properties.Source = WidgetSource.GpuPowerWatts;
            Check(widget.TextTemplate == "GPU {value} W" && widget.GaugeMaximum == 450, "power source supplies units/range without losing graph kind");
            Check(widget.Kind == kind, "numeric source switch preserves graph kind");
            properties.HistoryMinutes = 30; properties.AutoScale = false; properties.LineWidth = 3;
            Check(widget.GraphMinutes == 30 && !widget.GraphAutoScale && widget.GraphLineWidth == 3, "graph properties update profile");
        }
        var normalized = Monitoring.NormalizeReading("Package", LibreHardwareMonitor.Hardware.SensorType.Power, 120);
        Check(normalized is { Metric: SensorMetric.PowerWatts, Value: 120 }, "LHM power stays in watts, not MiB/temperature");
        string ledger = Path.Combine(directory, "test-energy-today.json");
        DailyEnergyStore.Save(telemetry.EnergyToday, ledger);
        Check(DailyEnergyStore.Load(ledger) == telemetry.EnergyToday, "daily checkpoint round-trip retains energy and separate coverage");
        Check(!File.Exists(ledger + ".tmp"), "atomic daily save leaves no temporary checkpoint");
        File.WriteAllText(ledger, "{broken"); Check(DailyEnergyStore.Load(ledger) is null, "corrupt checkpoint ignored without blocking sensors");
        File.WriteAllText(ledger, new string('x', 16385)); Check(DailyEnergyStore.Load(ledger) is null, "oversized checkpoint rejected");
        File.Delete(ledger);
        string rollingLedger = Path.Combine(directory, "test-energy-last24h.json");
        RollingEnergyStore.Save(telemetry.Energy24h, rollingLedger);
        var loaded = RollingEnergyStore.Load(rollingLedger);
        Check(loaded is { Valid: true } && Math.Abs(loaded.TotalKwh!.Value - telemetry.Energy24h.TotalKwh!.Value) < 1e-10 && loaded.TotalSeconds == telemetry.Energy24h.TotalSeconds,
            "rolling checkpoint retains observed totals and minute buckets");
        Check(!File.Exists(rollingLedger + ".tmp"), "atomic rolling checkpoint leaves no temporary file");
        File.WriteAllText(rollingLedger, "{broken"); Check(RollingEnergyStore.Load(rollingLedger) is null, "corrupt rolling checkpoint ignored");
        File.WriteAllText(rollingLedger, new string('x', 1024 * 1024 + 1)); Check(RollingEnergyStore.Load(rollingLedger) is null, "oversized rolling checkpoint rejected");
        File.Delete(rollingLedger);
        var narrowEnergy = new LcdWidget { Source = WidgetSource.EstimatedTotalEnergy24hProjection, Width = 128, Height = 45, FontSize = 12, TextTemplate = "PC 24h {value} kWh" };
        var migrated = new Settings(); migrated.Aio.Widgets = [narrowEnergy]; WidgetLayout.UpgradeEnergyWidgets(migrated);
        string narrowText = narrowEnergy.TextTemplate.Replace("{value}", "99.999").Replace("{coverage}", "1440m");
        using (var canvas = new Bitmap(128, 45))
        using (var graphics = Graphics.FromImage(canvas))
        using (var font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            var size = graphics.MeasureString(narrowText, font, int.MaxValue, StringFormat.GenericTypographic);
            Check(size.Width <= 128 && size.Height <= 45, "existing narrow energy widget fits total and coverage on two lines");
        }
        var sampleEnergy = telemetry with { Energy24h = new("demo", telemetry.Time, [new(new DateTimeOffset(telemetry.Time.UtcTicks - telemetry.Time.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero), 10, 60, 20, 60, 30, 60)]) };
        using (var image = renderer.Render(sampleEnergy, new() { UseCustomLayout = true, Widgets = [narrowEnergy] }, 480, 480))
            image.Save(Path.Combine(directory, "energy-widget-128x45.png"), ImageFormat.Png);
        var graphLayout = LcdPresets.Create(new(), "wide-power", 480, 128);
        using var process = Process.GetCurrentProcess(); int before = GetGuiResources(process.Handle, 0); long allocations = GC.GetTotalAllocatedBytes(false);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) { using var frame = renderer.Render(telemetry, graphLayout, 480, 128); }
        watch.Stop(); int after = GetGuiResources(process.Handle, 0);
        Check(after <= before + 2, "repeated three-graph rendering does not leak GDI resources");
        File.WriteAllText(Path.Combine(directory, "graph-render-cost.json"), JsonSerializer.Serialize(new { hardwareAccess = false, renders = 200, graphsPerRender = 3, historyPoints = telemetry.History.Points.Count, milliseconds = watch.Elapsed.TotalMilliseconds,
            millisecondsPerRender = watch.Elapsed.TotalMilliseconds / 200, allocationMiB = (GC.GetTotalAllocatedBytes(false) - allocations) / 1048576d, gdiBefore = before, gdiAfter = after }, Settings.JsonOptions));
        return checks;
    }
}
