namespace ThermalDesk.Core;

public sealed record PowerAverage(double? Watts, double CoveredSeconds)
{
    public static PowerAverage Empty { get; } = new(null, 0);
    public double? WattHours => Watts * CoveredSeconds / 3600;
    public string Coverage => CoveredSeconds < 60 ? $"{Math.Floor(CoveredSeconds):0}s" : $"{Math.Floor(CoveredSeconds / 60):0}m";
}
public sealed record HistoryPoint(DateTimeOffset Time, IReadOnlyList<double?> Values)
{
    private static readonly int SourceCount = Enum.GetValues<WidgetSource>().Length;
    public double? Value(WidgetSource source, int port = 1)
    {
        int index = port > 1 && source is WidgetSource.FanRpm or WidgetSource.FanDuty
            ? SourceCount + (port - 2) * 2 + (source == WidgetSource.FanDuty ? 1 : 0) : (int)source;
        return index >= 0 && index < Values.Count ? Values[index] : null;
    }
}
public sealed record HistorySnapshot(long Revision, IReadOnlyList<HistoryPoint> Points, double MaximumGapSeconds)
{
    public static HistorySnapshot Empty { get; } = new(0, [], 20);
}

public static class PowerEstimate
{
    public static Telemetry Apply(Telemetry telemetry, Settings settings)
    {
        bool includeGpu = settings.PowerEstimateIncludesGpu && telemetry.GpuAdapterKind != GpuAdapterKind.Integrated;
        double? cpu = telemetry.CpuPowerWatts, gpu = includeGpu ? telemetry.GpuPowerWatts : 0;
        double? estimated = cpu.HasValue && gpu.HasValue ? (cpu.Value + gpu.Value + settings.PowerEstimateExtraWatts) / (settings.PowerEstimateEfficiencyPercent / 100) : null;
        return telemetry with
        {
            EstimatedTotalPowerWatts = estimated,
            PowerEstimateStatus = estimated.HasValue
                ? $"ESTIMATE: (CPU package{(includeGpu ? " + selected GPU" : "") } + {settings.PowerEstimateExtraWatts:0.#} W other) / {settings.PowerEstimateEfficiencyPercent:0.#}% PSU efficiency. Not a wall meter; monitor/peripherals/other GPUs excluded."
                : "Total estimate unavailable: required CPU/GPU power sensor missing. No TDP/load substitution."
        };
    }
}

/// <summary>Single-writer sampler; immutable published snapshots. No disk, timers or extra hardware polling.</summary>
public sealed class TelemetryHistory(DailyEnergyState? restoredDailyEnergy = null, TimeZoneInfo? timezone = null, RollingEnergyState? restoredRollingEnergy = null)
{
    private readonly DailyEnergyCounter daily = new(restoredDailyEnergy, timezone);
    private readonly RollingEnergyCounter rolling = new(restoredRollingEnergy);
    public const int MaximumPoints = 724;
    private static readonly WidgetSource[] Sources = Enum.GetValues<WidgetSource>();
    private readonly Queue<HistoryPoint> points = new();
    private HistorySnapshot snapshot = HistorySnapshot.Empty;
    private PowerAverage cpuAverage = PowerAverage.Empty, gpuAverage = PowerAverage.Empty, totalAverage = PowerAverage.Empty;
    private string identity = "";
    private DateTimeOffset sampled;
    private long revision;
    public Telemetry Add(Telemetry raw, Settings settings)
    {
        var value = PowerEstimate.Apply(raw, settings);
        string key = FormattableString.Invariant($"{raw.GpuDeviceId}|{raw.CpuDeviceName}|{settings.PowerEstimateExtraWatts:R}|{settings.PowerEstimateEfficiencyPercent:R}|{settings.PowerEstimateIncludesGpu}");
        value = value with { EnergyToday = daily.Add(value, key, Math.Max(20, settings.PollSeconds * 3)),
            Energy24h = rolling.Add(value, key, Math.Max(20, settings.PollSeconds * 3)) };
        if (key != identity || raw.Time < sampled)
        { points.Clear(); snapshot = HistorySnapshot.Empty; cpuAverage = gpuAverage = totalAverage = PowerAverage.Empty; sampled = default; identity = key; }
        if (sampled == default || (raw.Time - sampled).TotalSeconds >= 5)
        {
            sampled = raw.Time;
            var values = new double?[Sources.Length + 8];
            foreach (var source in Sources)
            {
                double? number = WidgetLayout.NumericValue(source, value);
                values[(int)source] = number.HasValue && double.IsFinite(number.Value) ? number : null;
            }
            for (int port = 2; port <= 5; port++)
            {
                values[Sources.Length + (port - 2) * 2] = WidgetLayout.NumericValue(WidgetSource.FanRpm, value, port);
                values[Sources.Length + (port - 2) * 2 + 1] = WidgetLayout.NumericValue(WidgetSource.FanDuty, value, port);
            }
            points.Enqueue(new(raw.Time, Array.AsReadOnly(values)));
            var cutoff = raw.Time.AddHours(-1);
            // Keep one preceding point for exact clipping of the oldest interval.
            while (points.Count > 2 && points.ElementAt(1).Time <= cutoff) points.Dequeue();
            while (points.Count > MaximumPoints) points.Dequeue();
            snapshot = new(++revision, Array.AsReadOnly(points.ToArray()), Math.Max(20, settings.PollSeconds * 3));
            cpuAverage = Average(snapshot, WidgetSource.CpuPowerWatts, raw.Time);
            gpuAverage = Average(snapshot, WidgetSource.GpuPowerWatts, raw.Time);
            totalAverage = Average(snapshot, WidgetSource.EstimatedTotalPowerWatts, raw.Time);
            values[(int)WidgetSource.CpuPowerAverageHour] = cpuAverage.Watts;
            values[(int)WidgetSource.GpuPowerAverageHour] = gpuAverage.Watts;
            values[(int)WidgetSource.EstimatedTotalPowerAverageHour] = totalAverage.Watts;
        }
        return value with { History = snapshot, CpuPowerHour = cpuAverage, GpuPowerHour = gpuAverage, TotalPowerHour = totalAverage };
    }
    public static PowerAverage Average(HistorySnapshot history, WidgetSource source, DateTimeOffset now)
    {
        double weighted = 0, covered = 0; var cutoff = now.AddHours(-1);
        for (int i = 1; i < history.Points.Count; i++)
        {
            var a = history.Points[i - 1]; var b = history.Points[i];
            double duration = (b.Time - a.Time).TotalSeconds;
            var first = a.Value(source); var last = b.Value(source);
            if (duration <= 0 || duration > history.MaximumGapSeconds || first is null || last is null) continue;
            var from = a.Time < cutoff ? cutoff : a.Time; var to = b.Time > now ? now : b.Time;
            double seconds = (to - from).TotalSeconds; if (seconds <= 0) continue;
            double atFrom = first.Value + (last.Value - first.Value) * (from - a.Time).TotalSeconds / duration;
            double atTo = first.Value + (last.Value - first.Value) * (to - a.Time).TotalSeconds / duration;
            weighted += (atFrom + atTo) * .5 * seconds; covered += seconds;
        }
        return covered > 0 ? new(weighted / covered, covered) : PowerAverage.Empty;
    }
}

public sealed record GraphBucket(double X, double? Minimum, double? Maximum, double? Last, bool BreakBefore);
public static class GraphProjection
{
    // Bounded pixel buckets preserve spikes and gaps instead of allocating an
    // unbounded point list or smoothing missing readings into plausible data.
    public static IReadOnlyList<GraphBucket> Build(HistorySnapshot history, WidgetSource source, DateTimeOffset now, int minutes, int columns, int port = 1)
    {
        columns = Math.Clamp(columns, 2, 480);
        var cutoff = now.AddMinutes(-minutes); double window = minutes * 60;
        var result = new List<GraphBucket>(); int active = -1; double min = 0, max = 0, last = 0;
        bool breakNext = true, bucketBreak = true; DateTimeOffset previous = default;
        void Flush() { if (active >= 0) result.Add(new(active / (double)(columns - 1), min, max, last, bucketBreak)); }
        foreach (var point in history.Points)
        {
            if (point.Time < cutoff || point.Time > now) continue;
            double? value = point.Value(source, port);
            if (value is null || !double.IsFinite(value.Value)) { Flush(); active = -1; breakNext = true; previous = point.Time; continue; }
            if (previous != default && (point.Time - previous).TotalSeconds > history.MaximumGapSeconds) breakNext = true;
            int column = Math.Clamp((int)((point.Time - cutoff).TotalSeconds / window * (columns - 1)), 0, columns - 1);
            if (column != active || breakNext)
            { Flush(); active = column; min = max = last = value.Value; bucketBreak = breakNext; breakNext = false; }
            else { min = Math.Min(min, value.Value); max = Math.Max(max, value.Value); last = value.Value; }
            previous = point.Time;
        }
        Flush(); return result;
    }
}
