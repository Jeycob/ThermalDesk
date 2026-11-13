using System.Text.Json;
using ThermalDesk.Core;

internal static class WidgetSourceGaugeTests
{
    public static void Run(Action<bool, string> check)
    {
        var time = DateTimeOffset.UtcNow;
        HardwareSnapshot[] devices =
        [
            new("cpu", "CPU", DeviceCategory.Cpu,
            [new("Core #1", SensorMetric.ClockMhz, 4000), new("Core #2", SensorMetric.ClockMhz, 5000),
             new("Bus Speed", SensorMetric.ClockMhz, 100), new("Core Effective", SensorMetric.ClockMhz, 1000),
             new("Core SoC", SensorMetric.ClockMhz, 2000), new("Core Fabric", SensorMetric.ClockMhz, 2400),
             new("Core #3", SensorMetric.ClockMhz, double.NaN), new("Core #4", SensorMetric.ClockMhz, -1),
             new("Core #5", SensorMetric.ClockMhz, 20000), new("CPU (Tctl/Tdie)", SensorMetric.Temperature, 50)]),
            new("rtx", "NVIDIA GPU", DeviceCategory.NvidiaGpu,
            [new("GPU Memory Used", SensorMetric.DataGiB, 8), new("GPU Memory Total", SensorMetric.DataGiB, 16),
             new("GPU Memory Shared", SensorMetric.DataGiB, 30), new("GPU Memory", SensorMetric.Load, 99),
             new("GPU Core", SensorMetric.ClockMhz, 2505), new("GPU Memory", SensorMetric.ClockMhz, 11202),
             new("GPU Core", SensorMetric.Temperature, 42)]),
            new("igpu", "Integrated GPU", DeviceCategory.AmdGpu,
            [new("GPU Memory Used", SensorMetric.DataGiB, 1), new("GPU Memory Total", SensorMetric.DataGiB, 2),
             new("GPU Core", SensorMetric.ClockMhz, 800), new("GPU Memory", SensorMetric.ClockMhz, 3200)])
        ];
        var t = SensorProjection.Project(devices, time);
        check(t.CpuClockAverageMhz == 4500 && t.CpuClockMaximumMhz == 5000, "CPU clocks exclude bus/effective/fabric/invalid samples");
        check(t.GpuDeviceId == "rtx" && t.GpuVramUsedGiB == 8 && t.GpuVramTotalGiB == 16, "VRAM belongs to selected GPU");
        check(t.GpuVramUsedPercent == 50, "VRAM ratio uses dedicated capacity, not memory-controller load");
        check(t.GpuCoreClockMhz == 2505 && t.GpuMemoryClockMhz == 11202, "GPU reported clocks retain MHz units");
        var pinned = SensorProjection.Project(devices, time, "igpu");
        check(pinned.GpuVramUsedGiB == 1 && pinned.GpuVramTotalGiB == 2 && pinned.GpuCoreClockMhz == 800, "Pinned GPU owns all new readings");
        var absent = SensorProjection.Project(devices, time, "missing");
        check(absent.GpuVramUsedGiB is null && absent.GpuVramTotalGiB is null && absent.GpuVramUsedPercent is null && absent.GpuCoreClockMhz is null && absent.GpuMemoryClockMhz is null, "Absent pinned GPU does not borrow another GPU's memory/clocks");
        var empty = SensorProjection.Project([], time);
        check(empty.CpuClockAverageMhz is null && empty.CpuClockMaximumMhz is null, "Absent CPU clocks stay unavailable");
        var onlyShared = SensorProjection.Project([new("gpu", "GPU", DeviceCategory.NvidiaGpu,
            [new("D3D Shared Memory Used", SensorMetric.DataGiB, 12), new("GPU Memory", SensorMetric.Load, 75)])], time);
        check(onlyShared.GpuVramUsedGiB is null && onlyShared.GpuVramUsedPercent is null, "Shared-memory and utilization sensors are not fabricated into dedicated VRAM");
        foreach (var pair in new[] { (Used: 20d, Total: 16d), (Used: 1d, Total: 0d), (Used: double.NaN, Total: 16d) })
        {
            var invalid = SensorProjection.Project([new("gpu", "GPU", DeviceCategory.NvidiaGpu,
                [new("GPU Memory Used", SensorMetric.DataGiB, pair.Used), new("GPU Memory Total", SensorMetric.DataGiB, pair.Total)])], time);
            check(invalid.GpuVramUsedPercent is null, "Invalid VRAM pair cannot produce plausible percent");
        }
        var idle = SensorProjection.Project([new("gpu", "GPU", DeviceCategory.NvidiaGpu,
            [new("GPU Memory Used", SensorMetric.DataGiB, 0), new("GPU Memory Total", SensorMetric.DataGiB, 16), new("GPU Core", SensorMetric.ClockMhz, 0)])], time);
        check(idle.GpuVramUsedPercent == 0 && idle.GpuCoreClockMhz == 0, "Valid zero memory use / idle clock is not missing");
        (WidgetSource Source, double Value)[] expected =
        [(WidgetSource.GpuVramUsedGiB, 8), (WidgetSource.GpuVramTotalGiB, 16), (WidgetSource.GpuVramUsedPercent, 50),
         (WidgetSource.CpuClockAverageMhz, 4500), (WidgetSource.CpuClockMaximumMhz, 5000),
         (WidgetSource.GpuCoreClockMhz, 2505), (WidgetSource.GpuMemoryClockMhz, 11202)];
        foreach (var (source, value) in expected)
        {
            var widget = new LcdWidget { Source = source, TextTemplate = "{value}", ValueFormat = "0" };
            check(WidgetLayout.NumericValue(widget, t) == value, "New numeric source mapping: " + source);
            check(WidgetLayout.Format(widget, empty) == "—", "Missing new source uses dash: " + source);
        }
        check((int)WidgetKind.Text == 0 && (int)WidgetKind.RadialGauge == 1 && (int)WidgetSource.CpuMaximumTemperature == 12, "Old serialized enum values retained");
        var legacy = JsonSerializer.Deserialize<LcdWidget>("{\"Kind\":1,\"Source\":2}", Settings.JsonOptions)!;
        check(legacy.Kind == WidgetKind.RadialGauge && legacy.Source == WidgetSource.GpuTemperature && legacy.GaugeSegments == 24 && legacy.GaugeTextColor == "", "Old profiles default new gauge fields safely");
        foreach (var kind in Enum.GetValues<WidgetKind>())
        {
            var widget = new LcdWidget { Kind = kind, Width = 100, Height = 100, Source = WidgetSource.GpuVramUsedPercent, GaugeTextColor = "FFFFFF", GaugeSegments = 20 };
            var display = new DisplaySettings { Widgets = [widget] };
            WidgetLayout.Validate(display, 480, 480);
            var copy = display.Copy().Widgets.Single();
            check(copy.Kind == kind && copy.Source == WidgetSource.GpuVramUsedPercent && copy.GaugeSegments == 20 && copy.GaugeTextColor == "FFFFFF", "New gauge kind persists: " + kind);
            check(WidgetLayout.GaugeFraction(widget, t) == .5, "Gauge shared value normalization: " + kind);
            if (!WidgetLayout.IsGauge(kind) && !WidgetLayout.IsGraph(kind)) continue;
            widget.Source = WidgetSource.Clock;
            bool rejected = false; try { WidgetLayout.Validate(display, 480, 480); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Non-numeric source rejected for " + kind);
        }
        foreach (int count in new[] { 3, 81 })
        {
            bool rejected = false; try { WidgetLayout.Validate(new DisplaySettings { Widgets = [new() { GaugeSegments = count }] }, 480, 480); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Segment count outside 4–80 rejected");
        }
        var badColor = new DisplaySettings { Widgets = [new() { GaugeTextColor = "not-a-color" }] };
        bool colorRejected = false; try { WidgetLayout.Validate(badColor, 480, 480); } catch (ArgumentException) { colorRejected = true; }
        check(colorRejected, "Invalid optional gauge text color rejected");
    }
}
