namespace ThermalDesk.Core;

public static class TelemetryFreshness
{
    // Presentation follows the configured monitoring cadence (up to 10 s).
    // Cooling safety keeps its independent, fixed five-second guard.
    public static bool IsStale(Telemetry sample, DateTimeOffset now) =>
        sample.Time == DateTimeOffset.MinValue || now - sample.Time > TimeSpan.FromSeconds(Math.Max(5, Math.Clamp(sample.ExpectedSampleSeconds, 1, 10) + 1)) || sample.Time - now > TimeSpan.FromSeconds(1);

    public static string Status(Telemetry sample, DateTimeOffset now)
    {
        if (sample.Time == DateTimeOffset.MinValue) return "Waiting for sensor data";
        if (sample.Time - now > TimeSpan.FromSeconds(1)) return "Sensor timestamp is in the future";
        double age = Math.Max(0, (now - sample.Time).TotalSeconds);
        return IsStale(sample, now) ? $"SENSOR DATA OLD · {Math.Floor(age):0} s" : "";
    }

    public static bool UsesSensorSample(LcdWidget widget) => widget.Kind != WidgetKind.OnlineImage &&
        widget.Source is not (WidgetSource.Text or WidgetSource.Clock or WidgetSource.Date or WidgetSource.TopCpuProcess or WidgetSource.TopGpuProcess) &&
        !LogWidget.IsLog(widget.Source);
}
