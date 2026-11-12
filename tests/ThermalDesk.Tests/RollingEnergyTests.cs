using System.Text.Json;
using ThermalDesk.Core;

internal static class RollingEnergyTests
{
    internal static void Run(Action<bool, string> check)
    {
        var start = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        Telemetry Sample(DateTimeOffset time, double? watts = 1000) => new(time, 50, 40, 20, 30, 40)
        { CpuPowerWatts = watts, GpuPowerWatts = watts / 2, EstimatedTotalPowerWatts = watts * 2 };
        var counter = new RollingEnergyCounter(); var state = counter.Add(Sample(start), "one", 20);
        check(state.TotalKwh is null, "Rolling energy starts unavailable, never projected from a single reading");
        for (int i = 1; i <= 25 * 360; i++) state = counter.Add(Sample(start.AddSeconds(i * 10)), "one", 20);
        check(Math.Abs(state.CpuKwh!.Value - 24) < .000001 && Math.Abs(state.TotalKwh!.Value - 48) < .000001, "Rolling 24h expires old energy, rather than retaining lifetime totals");
        check(state.CpuSeconds == 86400 && state.Buckets.Count <= 1441 && state.Valid, "24h minute ledger stays bounded with separate observed coverage");
        var saved = JsonSerializer.Deserialize<RollingEnergyState>(JsonSerializer.Serialize(state, Settings.JsonOptions), Settings.JsonOptions)!;
        var resumed = new RollingEnergyCounter(saved);
        var resumeTime = start.AddHours(26);
        state = resumed.Add(Sample(resumeTime), "one", 20);
        check(Math.Abs(state.CpuKwh!.Value - 23) < .000001 && state.CpuSeconds == 23 * 3600, "Restart keeps rolling history but never invents energy during downtime");
        state = resumed.Add(Sample(resumeTime.AddSeconds(10)), "one", 20);
        check(state.CpuSeconds == 23 * 3600, "Window advances and adds only the fresh observed pair");
        state = resumed.Add(Sample(resumeTime.AddSeconds(20), null), "one", 20);
        check(state.CpuSeconds < 23 * 3600, "Missing samples reduce coverage, not counted as zero consumption");
        state = resumed.Add(Sample(resumeTime.AddSeconds(30)), "changed", 20);
        check(state.CpuKwh is null && state.Buckets.Count == 0, "Calibration/hardware changes reset incompatible rolling totals");
        state = resumed.Add(Sample(start), "changed", 20);
        check(state.CpuKwh is null, "Clock rollback resets instead of double counting");
        var crossing = new RollingEnergyCounter(); crossing.Add(Sample(start.AddSeconds(-5), 0), "one", 20);
        state = crossing.Add(Sample(start.AddSeconds(5), 1000), "one", 20);
        check(Math.Abs(state.CpuKwh!.Value - .5 * 10 / 3600) < .000001 && state.Buckets.Count == 2, "Trapezoidal integration splits minute crossings correctly");
        check(!(state with { Buckets = [state.Buckets[0], state.Buckets[0]] }).Valid, "Duplicate/non-monotonic persisted buckets rejected");
        check(!(state with { Buckets = [state.Buckets[0] with { CpuWh = double.NaN }] }).Valid, "NaN energy checkpoint rejected");
        var display = Sample(start) with { Energy24h = state };
        check(WidgetLayout.NumericValue(WidgetSource.CpuEnergyLast24hKwh, display) == state.CpuKwh, "Rolling widgets use observed kWh, not last-hour extrapolation");
        var settings = new Settings(); settings.Aio.Widgets = [new() { Source = WidgetSource.EstimatedTotalEnergy24hProjection }];
        WidgetLayout.UpgradeEnergyWidgets(settings);
        check(settings.Aio.Widgets[0].Source == WidgetSource.EstimatedTotalEnergyLast24hKwh && settings.Aio.Widgets[0].TextTemplate.Contains("{coverage}"), "Legacy projected widgets upgrade to accumulated 24h sources");
        check(WidgetLayout.RollingEnergyTemplate(WidgetSource.EstimatedTotalEnergyLast24hKwh, 128).Contains('\n'), "Narrow 24h widgets separate total from covered time");
        check(!JsonSerializer.Serialize(display, Settings.JsonOptions).Contains("Buckets"), "Large rolling ledger is not repeated in UI diagnostics");
    }
}
