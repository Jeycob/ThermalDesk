using System.Text.Json;
using ThermalDesk.Core;

internal static class PowerAndGraphTests
{
    internal static void Run(Action<bool, string> check)
    {
        var start = DateTimeOffset.Parse("2026-11-12T10:00:00Z"); var settings = new Settings();
        Telemetry Sample(int seconds, double? cpu = 100, double? gpu = 200) => new(start.AddSeconds(seconds), 50, 40, 20, 30, 40)
            { CpuPowerWatts = cpu, GpuPowerWatts = gpu, GpuDeviceId = "gpu1", CpuDeviceName = "CPU", Fans = [new(1, 800, 40), new(3, 1500, 60)] };
        var cpu = new HardwareSnapshot("cpu", "CPU", DeviceCategory.Cpu, [new("Package", SensorMetric.PowerWatts, 100), new("Core #1", SensorMetric.PowerWatts, 10), new("SoC", SensorMetric.PowerWatts, 20)]);
        var gpu = new HardwareSnapshot("gpu", "GPU", DeviceCategory.IntelGpu, [new("GPU Package", SensorMetric.PowerWatts, 80), new("GPU Total", SensorMetric.PowerWatts, 100), new("GPU Core", SensorMetric.PowerWatts, 60)]);
        var projected = SensorProjection.Project([cpu, gpu], start);
        check(projected.CpuPowerWatts == 100, "CPU package power does not double-count core/SoC sensors");
        check(projected.GpuPowerWatts == 100 && projected.GpuPowerSource == "GPU Total", "GPU total takes precedence over overlapping package/core");
        check(SensorProjection.Project([cpu with { Sensors = [new("Core #1", SensorMetric.PowerWatts, 10)] }], start).CpuPowerWatts is null, "Per-core or TDP is not substituted for package power");
        check(SensorProjection.Project([cpu with { Sensors = [new("CPU Package", SensorMetric.PowerWatts, 0)] }], start).CpuPowerWatts == 0, "Valid zero package power is retained");
        check(SensorProjection.Project([cpu with { Sensors = [new("Package", SensorMetric.PowerWatts, double.NaN)] }], start).CpuPowerWatts is null, "Invalid package power rejected");
        check(Math.Abs(PowerEstimate.Apply(Sample(0), settings).EstimatedTotalPowerWatts!.Value - 340 / .9) < .001, "Whole PC estimate includes explicit overhead and PSU efficiency");
        check(PowerEstimate.Apply(Sample(0, null), settings).EstimatedTotalPowerWatts is null && PowerEstimate.Apply(Sample(0, 100, null), settings).EstimatedTotalPowerWatts is null, "Missing required power does not become a fake total");
        check(Math.Abs(PowerEstimate.Apply(Sample(0) with { GpuAdapterKind = GpuAdapterKind.Integrated }, settings).EstimatedTotalPowerWatts!.Value - 140 / .9) < .001, "Known iGPU power is not double-counted with CPU package");
        var cpuOnly = settings.Copy(); cpuOnly.PowerEstimateIncludesGpu = false;
        check(PowerEstimate.Apply(Sample(0, 100, null), cpuOnly).EstimatedTotalPowerWatts.HasValue, "Explicit CPU-only estimate works without GPU power");
        var history = new TelemetryHistory(); var first = history.Add(Sample(0, 0), settings);
        check(first.CpuPowerHour.Watts is null && first.CpuPowerHour.CoveredSeconds == 0, "First reading does not invent a one-hour average");
        check(ReferenceEquals(first.History, history.Add(Sample(2, 100), settings).History), "History sampling capped to one point per five seconds");
        history.Add(Sample(10, 100), settings); var average = history.Add(Sample(30, 100), settings);
        check(Math.Abs(average.CpuPowerHour.Watts!.Value - 2500d / 30) < .001 && average.CpuPowerHour.CoveredSeconds == 30, "Power average is time-weighted across uneven sampling intervals");
        check(Math.Abs(average.CpuPowerHour.WattHours!.Value - 2500d / 3600) < .001, "Watts and accumulated watt-hours are not confused");
        var gap = history.Add(Sample(1000, null), settings);
        check(gap.CpuPowerHour.CoveredSeconds == 30, "Long suspend/missing interval not bridged into power average");
        history.Add(Sample(1010, 200), settings); gap = history.Add(Sample(1020, 200), settings);
        check(gap.CpuPowerHour.CoveredSeconds == 40, "Null endpoints exclude only the missing interval");
        check(!JsonSerializer.Serialize(gap, Settings.JsonOptions).Contains("\"history\"", StringComparison.OrdinalIgnoreCase), "History excluded from telemetry diagnostic JSON");
        var changed = settings.Copy(); changed.PowerEstimateExtraWatts = 80;
        check(history.Add(Sample(1030), changed).CpuPowerHour.CoveredSeconds == 0, "Calibration change resets history rather than mixing estimates");
        check(history.Add(Sample(1000), changed).History.Points.Count == 1, "Backward clock resets the timeline");
        var full = new TelemetryHistory(); Telemetry last = Sample(0);
        for (int i = 0; i <= 8000; i++) last = full.Add(Sample(i), settings);
        check(last.History.Points.Count <= TelemetryHistory.MaximumPoints && last.CpuPowerHour.CoveredSeconds == 3600, "Long sessions keep bounded history and an exact last-hour window");
        check(last.CpuPowerHour.Coverage == "60m" && new PowerAverage(10, 3599).Coverage == "59m", "Partial-hour coverage is not rounded up to a full hour");
        check(last.History.Points.Last().Value(WidgetSource.FanRpm, 3) == 1500, "Fan graph history uses selected physical port, not always port one");
        check(last.History.Points.Last().Value(WidgetSource.CpuPowerAverageHour) == 100, "Power-average source can also have graph history");
        var graph = GraphProjection.Build(last.History, WidgetSource.CpuPowerWatts, last.Time, 15, 50);
        check(graph.Count > 0 && graph.Count <= 50 && graph.All(b => b.X is >= 0 and <= 1), "Graph projection bounded to visible pixel columns");
        var spikes = new TelemetryHistory(); spikes.Add(Sample(0, 10), settings); spikes.Add(Sample(5, 1000), settings); var spikeData = spikes.Add(Sample(10, 20), settings);
        var compressed = GraphProjection.Build(spikeData.History, WidgetSource.CpuPowerWatts, spikeData.Time, 60, 2);
        check(compressed.Max(p => p.Maximum) == 1000, "Graph pixel buckets preserve brief power spikes");
        var missing = spikes.Add(Sample(15, null), settings); var resumed = spikes.Add(Sample(20, 30), settings);
        check(GraphProjection.Build(resumed.History, WidgetSource.CpuPowerWatts, resumed.Time, 1, 60).Last().BreakBefore, "Graph does not connect through a missing sample");
        check(WidgetLayout.IsGraph(WidgetKind.LineGraph) && !WidgetLayout.IsGauge(WidgetKind.LineGraph), "Graph kinds are not treated as progress gauges");
        var widget = new LcdWidget { Source = WidgetSource.CpuPowerAverageHour, TextTemplate = "AVG {value} W / {coverage}" };
        check(WidgetLayout.Format(widget, average).Contains("30s"), "Power widget can show actual sampled coverage");
        var invalid = new DisplaySettings { Widgets = [new() { Kind = WidgetKind.LineGraph, Source = WidgetSource.Text }] };
        bool rejected = false; try { WidgetLayout.Validate(invalid, 480, 128); } catch (ArgumentException) { rejected = true; }
        check(rejected, "Text/log source rejected for numeric graph");
        invalid.Widgets[0].Source = WidgetSource.CpuPowerWatts; invalid.Widgets[0].GraphMinutes = 61;
        rejected = false; try { WidgetLayout.Validate(invalid, 480, 128); } catch (ArgumentException) { rejected = true; }
        check(rejected, "Oversized graph history rejected");
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings, Settings.JsonOptions), Settings.JsonOptions)!;
        check(restored.PowerEstimateExtraWatts == 40 && restored.PowerEstimateEfficiencyPercent == 90, "Power calibration round-trips in profile");
    }
}
