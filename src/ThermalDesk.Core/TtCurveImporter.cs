using System.Globalization;

namespace ThermalDesk.Core;

// TT CUSTOMIZE stores nine duty samples corresponding to the graph's 20..100 C
// ticks. Import only this numeric curve, not a vendor binary or private profile.
public static class TtCurveImporter
{
    public static List<CurvePoint> Parse(string samples)
    {
        if (samples is null || samples.Length > 256) throw new ArgumentException("Invalid TT curve text.");
        var fields = samples.Split(',');
        if (fields.Length != 9) throw new ArgumentException("A TT custom curve requires nine duty samples for 20–100 C.");
        var points = new List<CurvePoint>();
        for (int i = 0; i < fields.Length; i++)
        {
            if (!double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double duty) ||
                !double.IsFinite(duty) || duty is < 30 or > 100)
                throw new ArgumentException("Imported duty must be 30–100%. Fan-stop values are not imported.");
            points.Add(new(20 + 10 * i, duty));
        }
        return points;
    }
}
