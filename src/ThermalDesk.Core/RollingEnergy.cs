using System.Text.Json.Serialization;

namespace ThermalDesk.Core;

// UTC minute buckets bound memory and checkpoint size. Only observed intervals
// are integrated. The oldest partial bucket is prorated (at most one minute).
public sealed record EnergyBucket(DateTimeOffset Minute, double CpuWh, double CpuSeconds,
    double GpuWh, double GpuSeconds, double TotalWh, double TotalSeconds);

public sealed record RollingEnergyState(string Identity, DateTimeOffset AsOf, IReadOnlyList<EnergyBucket> Buckets)
{
    public static RollingEnergyState Empty { get; } = new("", default, []);
    [JsonIgnore] public double? CpuKwh => CpuSeconds > 0 ? Sum(b => b.CpuWh) / 1000 : null;
    [JsonIgnore] public double? GpuKwh => GpuSeconds > 0 ? Sum(b => b.GpuWh) / 1000 : null;
    [JsonIgnore] public double? TotalKwh => TotalSeconds > 0 ? Sum(b => b.TotalWh) / 1000 : null;
    [JsonIgnore] public double CpuSeconds => Sum(b => b.CpuSeconds);
    [JsonIgnore] public double GpuSeconds => Sum(b => b.GpuSeconds);
    [JsonIgnore] public double TotalSeconds => Sum(b => b.TotalSeconds);
    private double Sum(Func<EnergyBucket, double> value)
    {
        if (AsOf < DateTimeOffset.MinValue.AddDays(2)) return 0;
        var cutoff = AsOf.AddHours(-24); double sum = 0;
        foreach (var b in Buckets)
        {
            double fraction = Math.Clamp((b.Minute.AddMinutes(1) - cutoff).TotalSeconds / 60, 0, 1);
            sum += value(b) * fraction;
        }
        return sum;
    }
    [JsonIgnore] public bool Valid
    {
        get
        {
            if (Identity is not { Length: > 0 and <= 1000 } || AsOf < DateTimeOffset.MinValue.AddDays(2) || AsOf > DateTimeOffset.MaxValue.AddDays(-2) || Buckets is null || Buckets.Count > 1441) return false;
            DateTimeOffset previous = default;
            foreach (var b in Buckets)
            {
                if (b is null || b.Minute.Offset != TimeSpan.Zero || b.Minute.Ticks % TimeSpan.TicksPerMinute != 0 || b.Minute <= previous ||
                    b.Minute > AsOf || b.Minute.AddMinutes(1) <= AsOf.AddHours(-24)) return false;
                if (new[] { b.CpuWh, b.GpuWh, b.TotalWh }.Any(v => !double.IsFinite(v) || v is < 0 or > 10000) ||
                    new[] { b.CpuSeconds, b.GpuSeconds, b.TotalSeconds }.Any(v => !double.IsFinite(v) || v is < 0 or > 60.000001)) return false;
                previous = b.Minute;
            }
            return true;
        }
    }
}

public sealed class RollingEnergyCounter
{
    private readonly LinkedList<EnergyBucket> buckets = new();
    private string identity;
    private Telemetry? previous;
    private DateTimeOffset latest, publishedAt;
    private RollingEnergyState snapshot = RollingEnergyState.Empty;
    public RollingEnergyCounter(RollingEnergyState? restored = null)
    {
        identity = restored is { Valid: true } ? restored.Identity : "";
        if (restored is { Valid: true }) { foreach (var bucket in restored.Buckets) buckets.AddLast(bucket); latest = restored.AsOf; }
    }
    public RollingEnergyState Add(Telemetry value, string key, double maximumGapSeconds)
    {
        bool reset = identity != key || value.Time < latest;
        if (reset) { buckets.Clear(); previous = null; publishedAt = default; identity = key; }
        var cutoff = value.Time.AddHours(-24);
        while (buckets.First is { } first && first.Value.Minute.AddMinutes(1) <= cutoff) buckets.RemoveFirst();
        if (previous is { } before)
        {
            double duration = (value.Time - before.Time).TotalSeconds;
            if (duration > 0 && duration <= maximumGapSeconds)
            {
                var from = before.Time.ToUniversalTime(); var end = value.Time.ToUniversalTime();
                while (from < end)
                {
                    var minute = new DateTimeOffset(from.Ticks - from.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
                    var to = minute.AddMinutes(1) < end ? minute.AddMinutes(1) : end;
                    double seconds = (to - from).TotalSeconds;
                    (double Wh, double Seconds) Part(double? a, double? b)
                    {
                        if (a is null || b is null || !double.IsFinite(a.Value) || !double.IsFinite(b.Value) || a < 0 || b < 0) return (0, 0);
                        double left = a.Value + (b.Value - a.Value) * (from - before.Time).TotalSeconds / duration;
                        double right = a.Value + (b.Value - a.Value) * (to - before.Time).TotalSeconds / duration;
                        return ((left + right) * .5 * seconds / 3600, seconds);
                    }
                    var cpu = Part(before.CpuPowerWatts, value.CpuPowerWatts); var gpu = Part(before.GpuPowerWatts, value.GpuPowerWatts);
                    var total = Part(before.EstimatedTotalPowerWatts, value.EstimatedTotalPowerWatts);
                    var bucket = buckets.Last?.Value.Minute == minute ? buckets.Last.Value : new EnergyBucket(minute, 0, 0, 0, 0, 0, 0);
                    var updated = bucket with { CpuWh = bucket.CpuWh + cpu.Wh, CpuSeconds = bucket.CpuSeconds + cpu.Seconds,
                        GpuWh = bucket.GpuWh + gpu.Wh, GpuSeconds = bucket.GpuSeconds + gpu.Seconds, TotalWh = bucket.TotalWh + total.Wh, TotalSeconds = bucket.TotalSeconds + total.Seconds };
                    if (buckets.Last?.Value.Minute == minute) buckets.Last.Value = updated; else buckets.AddLast(updated);
                    from = to;
                }
            }
        }
        previous = value; latest = value.Time;
        if (publishedAt == default || (value.Time - publishedAt).TotalSeconds >= 5)
        { publishedAt = value.Time; snapshot = new(identity, value.Time, Array.AsReadOnly(buckets.ToArray())); }
        return snapshot;
    }
}
