using System.Diagnostics;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// A preview of the exact planned frame, not a claim of physical LED feedback.
internal sealed class RgbPreview(Func<PortSettings> read, Func<Telemetry> telemetry, string fallback) : Control
{
    private readonly System.Windows.Forms.Timer tick = new() { Interval = 250 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    protected override void OnCreateControl()
    {
        base.OnCreateControl(); DoubleBuffered = true;
        tick.Tick += (_, _) => { if (Visible) Invalidate(); };
        tick.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.Clear(Color.FromArgb(10, 17, 29));
        using var font = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Point);
        try
        {
            var p = read();
            if (!p.Rgb) { e.Graphics.DrawString("RGB disabled · no lighting writes", font, Brushes.Silver, 5, 9); return; }
            var colors = Lighting.Frame(p, fallback, clock.Elapsed.TotalSeconds, telemetry());
            e.Graphics.DrawString($"Planned RGB · {p.RgbEffect} · {colors.Length} LEDs · not hardware feedback", font, Brushes.Silver, 5, 0);
            float width = Math.Max(1, (ClientSize.Width - 10f) / colors.Length);
            for (int i = 0; i < colors.Length; i++)
            {
                var c = colors[i]; using var brush = new SolidBrush(Color.FromArgb(c.Red, c.Green, c.Blue));
                e.Graphics.FillRectangle(brush, 5 + i * width, 22, Math.Max(1, width - 1), Math.Max(1, ClientSize.Height - 25));
            }
        }
        catch (Exception) { e.Graphics.DrawString("RGB preview pending · finish color / palette / period", font, Brushes.Orange, 5, 9); }
    }
    protected override void Dispose(bool disposing) { if (disposing) tick.Dispose(); base.Dispose(disposing); }
}
