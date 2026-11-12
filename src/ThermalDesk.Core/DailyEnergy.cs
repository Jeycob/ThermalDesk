using System.Globalization;

namespace ThermalDesk.Core;

public sealed record DailyEnergyState(string Day, string Timezone, string Identity,
    double CpuWattHours = 0, double CpuSeconds = 0, double GpuWattHours = 0, double GpuSeconds = 0, double TotalWattHours = 0, double TotalSeconds = 0)
{
    public static DailyEnergyState Empty { get; } = new("", "", "");
    public double? CpuKwh => CpuSeconds > 0 ? CpuWattHours / 1000 : null;
    public double? GpuKwh => GpuSeconds > 0 ? GpuWattHours / 1000 : null;
    public double? TotalKwh => TotalSeconds > 0 ? TotalWattHours / 1000 : null;
    public bool Valid => Day is { Length: 10 } && Timezone is { Length: <= 150 } && Identity is { Length: <= 1000 } &&
        new[] { CpuWattHours, GpuWattHours, TotalWattHours }.All(v => double.IsFinite(v) && v is >= 0 and <= 1000000) &&
        new[] { CpuSeconds, GpuSeconds, TotalSeconds }.All(v => double.IsFinite(v) && v is >= 0 and <= 90001);
}

// Constant-memory daily integration. No extrapolation through missing/offline time.
public sealed class DailyEnergyCounter(DailyEnergyState? restored = null, TimeZoneInfo? timezone = null)
{
    private readonly TimeZoneInfo zone = timezone ?? TimeZoneInfo.Local;
    private DailyEnergyState state = restored is { Valid: true } ? restored : DailyEnergyState.Empty;
    private Telemetry? previous;
    public DailyEnergyState Add(Telemetry value, string identity, double maximumGapSeconds)
    {
        var local = TimeZoneInfo.ConvertTime(value.Time, zone);
        string day = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool sameIdentity = state.Identity == identity && state.Timezone == zone.Id;
        bool backwards = previous is not null && value.Time < previous.Time;
        if (state.Day != day || !sameIdentity || backwards) state = new(day, zone.Id, identity);
        if (previous is { } before && sameIdentity && !backwards)
        {
            double span = (value.Time - before.Time).TotalSeconds;
            if (span > 0 && span <= maximumGapSeconds)
            {
                var midnight = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
                var from = before.Time < midnight ? midnight : before.Time;
                double seconds = (value.Time - from).TotalSeconds;
                (double Wh, double Covered) Integrate(double? start, double? end)
                {
                    if (seconds <= 0 || start is null || end is null || !double.IsFinite(start.Value) || !double.IsFinite(end.Value) || start < 0 || end < 0) return (0, 0);
                    double atFrom = start.Value + (end.Value - start.Value) * (from - before.Time).TotalSeconds / span;
                    return ((atFrom + end.Value) * .5 * seconds / 3600, seconds);
                }
                var cpu = Integrate(before.CpuPowerWatts, value.CpuPowerWatts); var gpu = Integrate(before.GpuPowerWatts, value.GpuPowerWatts);
                var total = Integrate(before.EstimatedTotalPowerWatts, value.EstimatedTotalPowerWatts);
                state = state with { CpuWattHours = state.CpuWattHours + cpu.Wh, CpuSeconds = state.CpuSeconds + cpu.Covered,
                    GpuWattHours = state.GpuWattHours + gpu.Wh, GpuSeconds = state.GpuSeconds + gpu.Covered,
                    TotalWattHours = state.TotalWattHours + total.Wh, TotalSeconds = state.TotalSeconds + total.Covered };
            }
        }
        previous = value; return state;
    }
}
