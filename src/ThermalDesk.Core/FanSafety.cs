namespace ThermalDesk.Core;

public sealed record FanDecision(int? Duty, string Reason);
public sealed class FanSafety
{
    private double? previous;
    private double? heldTemperature;
    public FanDecision Evaluate(PortSettings p, Telemetry t, DateTimeOffset now, double seconds)
    {
        if (p.Mode == ControlMode.Preserve) { Reset(); return new(null, "Unmanaged"); }
        if (p.Role == PortRole.Unknown) throw new InvalidOperationException("Unknown wiring cannot be controlled.");
        // Zero/sentinel readings from an inaccessible sensor must not look like an idle CPU.
        bool Valid(double? v) => v.HasValue && double.IsFinite(v.Value) && v.Value is >= 5 and <= 115;
        if (now - t.Time > TimeSpan.FromSeconds(5) || t.Time > now + TimeSpan.FromSeconds(1)) return Release("Stale telemetry");
        if (!Valid(t.CpuTemperature)) return Release("CPU sensor unavailable");
        if (t.CpuTemperature >= 85 || (Valid(t.GpuTemperature) && t.GpuTemperature >= 85)) return Full("Critical temperature");
        double? temperature = p.Source switch
        {
            TemperatureSource.Cpu => t.CpuTemperature,
            TemperatureSource.Gpu => t.GpuTemperature,
            _ => Valid(t.GpuTemperature) ? Math.Max(t.CpuTemperature!.Value, t.GpuTemperature!.Value) : null
        };
        if (p.Mode == ControlMode.Curve && !Valid(temperature)) return Release("Selected sensor unavailable");
        double target;
        if (p.Mode == ControlMode.Fixed) target = p.FixedDuty;
        else
        {
            // Increases are immediate; decreases require a 2 °C drop.
            if (heldTemperature is null || temperature > heldTemperature || heldTemperature - temperature >= 2) heldTemperature = temperature;
            target = Interpolate(p.Curve, heldTemperature!.Value);
        }
        int floor = p.Role == PortRole.Pump ? Math.Max(60, p.MinimumDuty) : Math.Max(30, p.MinimumDuty);
        target = Math.Clamp(target, floor, 100);
        // Never slew-limit safety responses. Normal reductions are deliberately slow.
        if (previous.HasValue && target < previous) target = Math.Max(target, previous.Value - 3 * Math.Clamp(seconds, 0, 10));
        previous = target;
        return new((int)Math.Ceiling(target), p.Mode == ControlMode.Fixed ? "Fixed" : $"Curve · {heldTemperature:0.0} °C");
    }
    private FanDecision Full(string reason) { previous = 100; heldTemperature = null; return new(100, reason); }
    private FanDecision Release(string reason) => new(null, reason + " · release software control; do not force full speed");
    public void Reset() { previous = heldTemperature = null; }
    public static double Interpolate(IReadOnlyList<CurvePoint> points, double value)
    {
        if (!double.IsFinite(value) || points.Count < 2) throw new ArgumentException("Invalid curve input.");
        if (value <= points[0].Temperature) return points[0].Duty;
        for (int i = 1; i < points.Count; i++)
            if (value <= points[i].Temperature)
                return points[i - 1].Duty + (points[i].Duty - points[i - 1].Duty) * (value - points[i - 1].Temperature) / (points[i].Temperature - points[i - 1].Temperature);
        return points[^1].Duty;
    }
}
