using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class FrameRenderer : IDisposable
{
    private Image? media;
    private string loadedPath = "";
    private DateTime modified;
    private DateTimeOffset opened;
    public Bitmap Render(Telemetry t, DisplaySettings settings, int width, int height)
    {
        int w = settings.Rotation is 90 or 270 ? height : width, h = settings.Rotation is 90 or 270 ? width : height;
        using var canvas = new Bitmap(w, h);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(ParseColor(settings.BackgroundColor));
            if (settings.Content == DisplayContent.Image)
            {
                var info = new FileInfo(settings.MediaPath);
                if (media is null || loadedPath != info.FullName || modified != info.LastWriteTimeUtc)
                {
                    if (!info.Exists || info.Length > 25 * 1024 * 1024) throw new IOException("Image must exist and be at most 25 MiB.");
                    using var source = Image.FromFile(info.FullName);
                    if (source.Width > 4096 || source.Height > 4096) throw new IOException("Image dimensions exceed 4096×4096.");
                    media?.Dispose(); media = (Image)source.Clone(); loadedPath = info.FullName; modified = info.LastWriteTimeUtc; opened = DateTimeOffset.UtcNow;
                }
                if (media.FrameDimensionsList.Contains(FrameDimension.Time.Guid))
                {
                    int count = media.GetFrameCount(FrameDimension.Time);
                    media.SelectActiveFrame(FrameDimension.Time, (int)((DateTimeOffset.UtcNow - opened).TotalSeconds * 5) % count);
                }
                DrawFit(g, media, w, h);
            }
            else if (settings.Content == DisplayContent.Screen)
            {
                var screen = Screen.PrimaryScreen ?? throw new IOException("No interactive desktop available.");
                using var capture = new Bitmap(screen.Bounds.Width, screen.Bounds.Height);
                using (var c = Graphics.FromImage(capture)) c.CopyFromScreen(screen.Bounds.Location, Point.Empty, screen.Bounds.Size);
                DrawFit(g, capture, w, h);
            }
            if (settings.Content == DisplayContent.Dashboard || settings.Overlay)
            {
                if (settings.UseCustomLayout) DrawWidgets(g, t, settings.Widgets);
                else DrawMetrics(g, t, w, h, settings.Content != DisplayContent.Dashboard);
            }
        }
        canvas.RotateFlip(settings.Rotation switch { 90 => RotateFlipType.Rotate90FlipNone, 180 => RotateFlipType.Rotate180FlipNone, 270 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone });
        return (Bitmap)canvas.Clone();
    }
    internal static Color ParseColor(string value) => ColorTranslator.FromHtml("#" + value.TrimStart('#'));
    private static void DrawWidgets(Graphics g, Telemetry t, IEnumerable<LcdWidget> widgets)
    {
        foreach (var widget in widgets)
        {
            var state = g.Save();
            try
            {
                var box = new Rectangle(widget.X, widget.Y, widget.Width, widget.Height);
                g.SetClip(box);
                if (widget.Background.Length > 0) { using var bg = new SolidBrush(ParseColor(widget.Background)); g.FillRectangle(bg, box); }
                if (widget.Kind == WidgetKind.RadialGauge)
                {
                    float size = Math.Min(box.Width, box.Height) - widget.GaugeThickness - 2;
                    var arc = new RectangleF(box.X + (box.Width - size) / 2, box.Y + (box.Height - size) / 2, size, size);
                    using var track = new Pen(ParseColor(widget.GaugeTrackColor), widget.GaugeThickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    using var progress = new Pen(ParseColor(widget.Foreground), widget.GaugeThickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawArc(track, arc, widget.GaugeStartAngle, widget.GaugeSweepAngle);
                    if (WidgetLayout.GaugeFraction(widget, t) is > 0 and var fraction)
                        g.DrawArc(progress, arc, widget.GaugeStartAngle, (float)fraction * widget.GaugeSweepAngle);
                    if (!widget.GaugeShowText) continue;
                }
                using var ink = new SolidBrush(ParseColor(widget.Foreground));
                using var font = new Font(widget.FontFamily, widget.FontSize, widget.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                using var format = new StringFormat { Alignment = widget.Alignment switch { WidgetAlignment.Left => StringAlignment.Near, WidgetAlignment.Right => StringAlignment.Far, _ => StringAlignment.Center }, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                g.DrawString(WidgetLayout.Format(widget, t), font, ink, box, format);
            }
            finally { g.Restore(state); }
        }
    }
    private static void DrawFit(Graphics g, Image image, int w, int h)
    {
        double scale = Math.Min((double)w / image.Width, (double)h / image.Height);
        int dw = (int)(image.Width * scale), dh = (int)(image.Height * scale);
        g.DrawImage(image, (w - dw) / 2, (h - dh) / 2, dw, dh);
    }
    private static void DrawMetrics(Graphics g, Telemetry t, int w, int h, bool overlay)
    {
        if (overlay) { using var bg = new SolidBrush(Color.FromArgb(190, 10, 17, 29)); g.FillRectangle(bg, 0, 0, w, h); }
        if (h >= w)
        {
            float s = w / 480f;
            using var ring = new Pen(Color.FromArgb(38, 218, 179), Math.Max(2, 4 * s));
            using var faint = new Pen(Color.FromArgb(36, 58, 73), Math.Max(1, 2 * s));
            using var title = new Font("Segoe UI", Math.Max(6, 22 * s), FontStyle.Bold);
            using var number = new Font("Segoe UI", Math.Max(12, 78 * s), FontStyle.Bold);
            using var detail = new Font("Segoe UI", Math.Max(6, 30 * s));
            using var clock = new Font("Segoe UI", Math.Max(6, 27 * s), FontStyle.Bold);
            using var ink = new SolidBrush(Color.FromArgb(151, 172, 191));
            using var teal = new SolidBrush(Color.FromArgb(38, 218, 179));
            float cy = h / 2f;
            void Center(string text, Font font, Brush brush, float y)
            { float textWidth = g.MeasureString(text, font).Width; g.DrawString(text, font, brush, (w - textWidth) / 2, y); }
            g.DrawEllipse(faint, 24 * s, cy - w / 2f + 24 * s, w - 48 * s, w - 48 * s);
            g.DrawArc(ring, 24 * s, cy - w / 2f + 24 * s, w - 48 * s, w - 48 * s, -90, (float)Math.Clamp(t.CpuTemperature ?? 0, 0, 100) * 3.6f);
            Center("THERMALDESK", title, teal, cy - 150 * s);
            Center("CPU TEMPERATURE", title, ink, cy - 100 * s);
            Center(t.CpuTemperature.HasValue ? $"{t.CpuTemperature:0}°" : "—", number, Brushes.White, cy - 73 * s);
            Center(t.GpuTemperature.HasValue ? $"GPU  {t.GpuTemperature:0} °C" : "GPU  —", detail, ink, cy + 44 * s);
            Center($"MEMORY  {t.MemoryUsedPercent:0}%", title, ink, cy + 86 * s);
            Center(DateTime.Now.ToString("HH:mm:ss"), clock, teal, cy + 125 * s);
            return;
        }
        float scale = Math.Min(w / 480f, h / 128f);
        using var small = new Font("Segoe UI", Math.Max(8, 10 * scale));
        using var large = new Font("Segoe UI", Math.Max(12, 29 * scale), FontStyle.Bold);
        using var heading = new Font("Segoe UI", Math.Max(8, 9 * scale), FontStyle.Bold);
        using var muted = new SolidBrush(Color.FromArgb(144, 168, 186));
        using var accent = new SolidBrush(Color.FromArgb(38, 218, 179));
        g.DrawString("THERMALDESK", heading, accent, 16, 8);
        var labels = new[] { "CPU", "GPU", "MEMORY" };
        var values = new[] { t.CpuTemperature, t.GpuTemperature, t.MemoryUsedPercent };
        float y = h > w ? h * .35f : 28, cell = w / 3f;
        for (int i = 0; i < 3; i++)
        {
            float x = cell * i + 16;
            g.DrawString(labels[i], small, muted, x, y);
            g.DrawString(values[i].HasValue ? $"{values[i]:0}{(i == 2 ? "%" : "°")}" : "—", large, Brushes.White, x, y + 16 * scale);
            using var track = new SolidBrush(Color.FromArgb(33, 49, 65));
            g.FillRectangle(track, x, y + 59 * scale, cell - 28, 3);
            if (values[i].HasValue) g.FillRectangle(accent, x, y + 59 * scale, (float)Math.Clamp(values[i]!.Value / 100, 0, 1) * (cell - 28), 3);
        }
        g.DrawString(DateTime.Now.ToString("HH:mm:ss"), small, muted, 16, h - 23);
    }
    public static byte[] Jpeg(Bitmap bitmap)
    {
        using var data = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().Single(x => x.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        bitmap.Save(data, codec, parameters); return data.ToArray();
    }
    public void Dispose() => media?.Dispose();
}
