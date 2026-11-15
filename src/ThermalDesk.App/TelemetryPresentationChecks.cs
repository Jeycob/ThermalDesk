using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class TelemetryPresentationChecks
{
    public static int Run(string directory)
    {
        int checks = 0;
        void Check(bool condition, string reason) { checks++; if (!condition) throw new InvalidOperationException("Telemetry presentation: " + reason); }
        var measured = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var sample = new Telemetry(measured, 55, 42, 10, 20, 30);
        var later = sample with { RenderTime = measured.AddSeconds(8) };
        Check(later.Time == measured, "rendering never advances sample time");
        Check(!TelemetryFreshness.IsStale(sample, measured.AddSeconds(5)), "five-second boundary remains valid");
        Check(TelemetryFreshness.IsStale(later, later.PresentationTime), "old samples are stale despite an advancing render clock");
        Check(TelemetryFreshness.IsStale(sample, measured.AddSeconds(-2)), "future sensor timestamps are rejected");
        Check(!TelemetryFreshness.IsStale(sample with { ExpectedSampleSeconds = 10 }, measured.AddSeconds(10)), "healthy slow monitoring cadence is not reported stale");
        Check(TelemetryFreshness.IsStale(sample with { ExpectedSampleSeconds = 10 }, measured.AddSeconds(12)), "overdue slow monitoring is reported stale");
        Check(TelemetryFreshness.Status(later, later.PresentationTime).Contains("8 s"), "stale label includes observed age");
        var clock = new LcdWidget { Source = WidgetSource.Clock, TextTemplate = "{value}", ValueFormat = "HH:mm:ss" };
        Check(WidgetLayout.Format(clock, later) == later.PresentationTime.LocalDateTime.ToString("HH:mm:ss"), "clock advances independently");
        var sensor = new LcdWidget { Source = WidgetSource.CpuTemperature, TextTemplate = "CPU {value} °C" };
        var display = new DisplaySettings { UseCustomLayout = true, Widgets = [sensor] };
        Check(FrameRenderer.VisualKey(sample, display) != FrameRenderer.VisualKey(later, display), "cached static sensor layout invalidates when stale");
        Check(FrameRenderer.VisualKey(later, display) != FrameRenderer.VisualKey(later with { RenderTime = measured.AddSeconds(9) }, display), "cached stale age keeps advancing");
        Check(FrameRenderer.FreshnessStatus(later, display).Length > 0, "sensor layouts show the stale warning");
        Check(FrameRenderer.FreshnessStatus(later with { Time = later.PresentationTime }, display).Length == 0, "fresh sample clears the warning");
        var clockOnly = new DisplaySettings { UseCustomLayout = true, Widgets = [clock] };
        Check(FrameRenderer.FreshnessStatus(later, clockOnly).Length == 0, "clock-only layouts do not claim missing sensor dependencies");
        var port = new PortSettings { Role = PortRole.Fan, Mode = ControlMode.Curve };
        Check(new FanSafety().Evaluate(port, later, later.PresentationTime, 1).Duty is null, "render clock cannot bypass the control stale guard");
        using (var renderer = new FrameRenderer())
        {
            using var wide = renderer.Render(later, display, 480, 128);
            wide.Save(Path.Combine(directory, "stale-sensors-wide.png"));
            using var round = renderer.Render(later, new DisplaySettings(), 480, 480);
            round.Save(Path.Combine(directory, "stale-sensors-round.png"));
        }
        using var form = new MainForm(new Settings(), Path.Combine(directory, "audit-ui-" + Guid.NewGuid().ToString("N") + ".json"));
        checks += form.ExerciseAuditPresentationForTest(directory);
        return checks;
    }
}
