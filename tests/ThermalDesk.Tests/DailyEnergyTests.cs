using System.Text.Json;
using ThermalDesk.Core;

internal static class DailyEnergyTests
{
    internal static void Run(Action<bool, string> check)
    {
        var start = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        Telemetry Sample(DateTimeOffset t, double? watts = 1000) => new(t, 50, 40, 20, 30, 40) { CpuPowerWatts = watts, GpuPowerWatts = watts / 2, EstimatedTotalPowerWatts = watts * 2 };
        var counter = new DailyEnergyCounter(timezone: TimeZoneInfo.Utc); var first = counter.Add(Sample(start), "one", 20);
        check(first.CpuKwh is null, "Daily energy needs two valid readings, not an invented zero day");
        DailyEnergyState state = first;
        for (int i = 1; i <= 360; i++) state = counter.Add(Sample(start.AddSeconds(i * 10)), "one", 20);
        check(Math.Abs(state.CpuKwh!.Value - 1) < .000001 && state.CpuSeconds == 3600, "1000 W for one hour equals 1 kWh");
        check(Math.Abs(state.GpuKwh!.Value - .5) < .000001 && Math.Abs(state.TotalKwh!.Value - 2) < .000001, "CPU, GPU and estimated whole-PC integrals stay separate");
        var saved = JsonSerializer.Deserialize<DailyEnergyState>(JsonSerializer.Serialize(state, Settings.JsonOptions), Settings.JsonOptions)!;
        var resumed = new DailyEnergyCounter(saved, TimeZoneInfo.Utc);
        state = resumed.Add(Sample(start.AddHours(2)), "one", 20);
        check(state.CpuSeconds == 3600 && Math.Abs(state.CpuKwh!.Value - 1) < .000001, "Restore retains day totals without integrating unobserved downtime");
        state = resumed.Add(Sample(start.AddHours(2).AddSeconds(10)), "one", 20);
        check(state.CpuSeconds == 3610, "Integration resumes from a fresh pair after restart");
        state = resumed.Add(Sample(start.AddHours(2).AddSeconds(20), null), "one", 20);
        check(state.CpuSeconds == 3610, "Missing endpoint is excluded, not counted as zero power");
        state = resumed.Add(Sample(start.AddHours(2).AddSeconds(30)), "two", 20);
        check(state.CpuKwh is null, "Changed calibration/device identity resets the daily model");
        var midnight = new DailyEnergyCounter(timezone: TimeZoneInfo.Utc);
        midnight.Add(Sample(DateTimeOffset.Parse("2026-11-12T23:59:58Z")), "one", 20);
        state = midnight.Add(Sample(DateTimeOffset.Parse("2026-11-13T00:00:02Z")), "one", 20);
        check(state.Day == "2026-11-13" && state.CpuSeconds == 2 && Math.Abs(state.CpuKwh!.Value - 2d / 3600) < .000001, "Midnight clips the crossing interval to the new calendar day");
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test +01", TimeSpan.FromHours(1), "Test", "Test");
        var local = new DailyEnergyCounter(timezone: zone);
        local.Add(Sample(DateTimeOffset.Parse("2026-11-12T22:59:58Z")), "one", 20);
        state = local.Add(Sample(DateTimeOffset.Parse("2026-11-12T23:00:02Z")), "one", 20);
        check(state.Day == "2026-11-13" && state.CpuSeconds == 2, "Calendar rollover follows configured local timezone, not UTC");
        var central = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
        foreach (var date in new[] { "2026-03-29", "2026-10-25" })
        {
            var dst = new DailyEnergyCounter(timezone: central);
            var edge = DateTimeOffset.Parse(date + "T00:59:58Z"); dst.Add(Sample(edge), "one", 20);
            check(dst.Add(Sample(edge.AddSeconds(4)), "one", 20).CpuSeconds == 4, "DST clock change counts elapsed UTC seconds only: " + date);
        }
        check(!new DailyEnergyState("2026-11-12", "UTC", "one", CpuWattHours: double.NaN).Valid, "Corrupt persisted totals are rejected");
        check(!new DailyEnergyState(null!, "UTC", "one").Valid, "Malformed daily state cannot crash startup");
        var projected = Sample(start) with { CpuPowerHour = new(100, 60), GpuPowerHour = new(200, 60), TotalPowerHour = new(400, 60), EnergyToday = state };
        check(WidgetLayout.NumericValue(WidgetSource.CpuEnergy24hProjection, projected) == 2.4 && WidgetLayout.NumericValue(WidgetSource.EstimatedTotalEnergy24hProjection, projected) == 9.6, "24h projection uses mean watts x 24 / 1000, not accumulated daily energy");
        var widget = new LcdWidget { Source = WidgetSource.CpuEnergyTodayKwh, TextTemplate = "TODAY {value} kWh /{coverage}", ValueFormat = "0.000" };
        check(WidgetLayout.Format(widget, projected).Contains("/2s"), "Daily widgets expose observed coverage");
    }
}
