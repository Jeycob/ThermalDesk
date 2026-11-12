namespace ThermalDesk.Core;

public readonly record struct LedColor(byte Red, byte Green, byte Blue)
{
    public static LedColor Parse(string hex)
    {
        if (!WidgetLayout.IsColor(hex)) throw new ArgumentException("Six-digit RGB hex required.");
        hex = hex.TrimStart('#'); return new(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }
    public LedColor Scale(double level) => new((byte)Math.Clamp(Math.Round(Red * level), 0, 255), (byte)Math.Clamp(Math.Round(Green * level), 0, 255), (byte)Math.Clamp(Math.Round(Blue * level), 0, 255));
}

public static class Lighting
{
    public static void ValidatePalette(PortSettings port)
    {
        if (!Enum.IsDefined(port.RgbDirection) || port.RgbPalette is null || port.RgbPalette.Count > 8 ||
            port.RgbPalette.Any(c => c is null || !WidgetLayout.IsColor(c)) ||
            (port.RgbPalette.Count == 1) || (port.RgbEffect == RgbEffect.PaletteWave && port.RgbPalette.Count < 2))
            throw new ArgumentException($"Port {port.Port}: RGB palette needs 2–8 six-digit hex colors; select a valid direction.");
    }
    public static LedColor[] Frame(PortSettings port, string fallback, double elapsedSeconds, Telemetry telemetry)
    {
        if (port.LedCount is < 1 or > 62 || !double.IsFinite(elapsedSeconds) || !double.IsFinite(port.RgbPeriodSeconds) || port.RgbPeriodSeconds is < 1 or > 60 || port.RgbBrightness is < 0 or > 100 || !Enum.IsDefined(port.RgbEffect)) throw new ArgumentException("Invalid lighting frame parameters.");
        ValidatePalette(port);
        double phase = ((elapsedSeconds / port.RgbPeriodSeconds) % 1 + 1) % 1;
        if (port.RgbDirection == RgbDirection.Reverse) phase = (1 - phase) % 1;
        double brightness = port.RgbBrightness / 100d;
        var color = LedColor.Parse(port.RgbColor.Length == 0 ? fallback : port.RgbColor);
        if (port.RgbEffect == RgbEffect.Breathing) brightness *= .08 + .92 * (.5 - .5 * Math.Cos(phase * Math.Tau));
        if (port.RgbEffect == RgbEffect.Blink && phase >= .5) brightness = 0;
        if (port.RgbEffect == RgbEffect.ColorCycle) color = Hsv(phase);
        if (port.RgbEffect == RgbEffect.Temperature)
        {
            double? temperature = port.Source switch { TemperatureSource.Cpu => telemetry.CpuTemperature, TemperatureSource.Gpu => telemetry.GpuTemperature, _ => telemetry.CpuTemperature.HasValue && telemetry.GpuTemperature.HasValue ? Math.Max(telemetry.CpuTemperature.Value, telemetry.GpuTemperature.Value) : null };
            color = temperature.HasValue && double.IsFinite(temperature.Value) ? Hsv((1 - Math.Clamp((temperature.Value - 30) / 55, 0, 1)) * .5) : new(255, 160, 0);
        }
        var colors = new LedColor[port.LedCount];
        var palette = port.RgbEffect == RgbEffect.PaletteWave ? port.RgbPalette.Select(LedColor.Parse).ToArray() : [];
        for (int i = 0; i < colors.Length; i++)
        {
            double position = (phase + (double)i / colors.Length) % 1;
            colors[i] = (port.RgbEffect switch
            {
                RgbEffect.RainbowWave => Hsv(position),
                RgbEffect.PaletteWave => InterpolatePalette(palette, position),
                _ => color
            }).Scale(brightness);
        }
        return colors;
    }
    private static LedColor InterpolatePalette(LedColor[] colors, double position)
    {
        double index = position * colors.Length; int start = (int)index; double amount = index - start;
        var a = colors[start % colors.Length]; var b = colors[(start + 1) % colors.Length];
        return new((byte)Math.Round(a.Red + (b.Red - a.Red) * amount),
            (byte)Math.Round(a.Green + (b.Green - a.Green) * amount),
            (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * amount));
    }
    private static LedColor Hsv(double hue)
    {
        double h = hue * 6; int sector = (int)h; byte down = (byte)Math.Round(255 * (1 - (h - sector))), up = (byte)Math.Round(255 * (h - sector));
        return (sector % 6) switch { 0 => new(255, up, 0), 1 => new(down, 255, 0), 2 => new(0, 255, up), 3 => new(0, down, 255), 4 => new(up, 0, 255), _ => new(255, 0, down) };
    }
}
