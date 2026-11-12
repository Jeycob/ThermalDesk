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
    private int mediaFrames = 1;
    private bool disposed;
    private readonly Dictionary<(string Family, float Size, FontStyle Style, GraphicsUnit Unit), Font> fonts = new();
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().Single(x => x.FormatID == ImageFormat.Jpeg.Guid);
    private readonly MemoryStream jpegBuffer = new(65536);
    private readonly EncoderParameters jpegParameters = new(1);
    public FrameRenderer() { jpegParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L); }
    private Font FontFor(string family, float size, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
    {
        var key = (family, size, style, unit);
        if (fonts.TryGetValue(key, out var cached)) return cached;
        if (fonts.Count >= 96) { foreach (var font in fonts.Values) font.Dispose(); fonts.Clear(); }
        return fonts[key] = new Font(family, size, style, unit);
    }
    private Font LogFont(Graphics graphics, LcdWidget widget, string text, Rectangle box)
    {
        FontStyle style = widget.Bold ? FontStyle.Bold : FontStyle.Regular;
        using var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap };
        bool Fits(float size)
        {
            var measured = graphics.MeasureString(text, FontFor(widget.FontFamily, size, style, GraphicsUnit.Pixel), new SizeF(100000, 100000), format);
            return measured.Width <= box.Width - 2 && measured.Height <= box.Height;
        }
        float low = Math.Min(8, widget.FontSize), high = widget.FontSize;
        if (Fits(high)) return FontFor(widget.FontFamily, high, style, GraphicsUnit.Pixel);
        for (int i = 0; i < 7 && high - low > .5f; i++) { float middle = (low + high) / 2; if (Fits(middle)) low = middle; else high = middle; }
        return FontFor(widget.FontFamily, (float)Math.Floor(low * 2) / 2, style, GraphicsUnit.Pixel);
    }
    internal SizeF MeasureLogText(Graphics graphics, LcdWidget widget, string text)
    {
        using var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap };
        return graphics.MeasureString(text, LogFont(graphics, widget, text, new(widget.X, widget.Y, widget.Width, widget.Height)), new SizeF(100000, 100000), format);
    }
    // Equality key describes visible values, not telemetry timestamps. Physical LCD
    // keepalives/transfers remain unchanged; only duplicate rendering/JPEG is skipped.
    internal static string? VisualKey(Telemetry telemetry, DisplaySettings settings)
    {
        if (settings.Content != DisplayContent.Dashboard) return null; // live capture / animated media must advance
        if (!settings.UseCustomLayout) return $"{telemetry.CpuTemperature:R}|{telemetry.GpuTemperature:R}|{telemetry.MemoryUsedPercent:R}|{DateTime.Now:yyyyMMddHHmmss}";
        return string.Join('\u001f', settings.Widgets.Select(w => WidgetLayout.Format(w, telemetry) +
            (WidgetLayout.IsGauge(w.Kind) ? "|" + WidgetLayout.GaugeFraction(w, telemetry)?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "") +
            (WidgetLayout.IsGraph(w.Kind) ? "|history:" + telemetry.History.Revision : "")));
    }
    public byte[] Encode(Bitmap bitmap)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        jpegBuffer.SetLength(0); jpegBuffer.Position = 0;
        bitmap.Save(jpegBuffer, JpegCodec, jpegParameters); return jpegBuffer.ToArray();
    }
    public Bitmap Render(Telemetry t, DisplaySettings settings, int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int w = settings.Rotation is 90 or 270 ? height : width, h = settings.Rotation is 90 or 270 ? width : height;
        var canvas = new Bitmap(w, h);
        try
        {
        if (settings.Content != DisplayContent.Image && media is not null)
        { media.Dispose(); media = null; loadedPath = ""; mediaFrames = 1; }
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
                    int frames = source.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? source.GetFrameCount(FrameDimension.Time) : 1;
                    if ((long)source.Width * source.Height * frames > 16 * 1024 * 1024) throw new IOException("Animated image exceeds the decoded-pixel budget (16 million pixels across all frames).");
                    var nextMedia = (Image)source.Clone(); media?.Dispose(); media = nextMedia; mediaFrames = frames;
                    loadedPath = info.FullName; modified = info.LastWriteTimeUtc; opened = DateTimeOffset.UtcNow;
                }
                if (mediaFrames > 1)
                {
                    media.SelectActiveFrame(FrameDimension.Time, (int)((DateTimeOffset.UtcNow - opened).TotalSeconds * 5) % mediaFrames);
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
        return canvas; // Transfer ownership; no second full-size bitmap allocation.
        }
        catch { canvas.Dispose(); throw; }
    }
    internal static Color ParseColor(string value) => ColorTranslator.FromHtml("#" + value.TrimStart('#'));
    internal static string FormatTemperature(double? value) => value.HasValue && double.IsFinite(value.Value) ? $"{value:0} °C" : "—";
    private void DrawWidgets(Graphics g, Telemetry t, IEnumerable<LcdWidget> widgets)
    {
        foreach (var widget in widgets)
        {
            var state = g.Save();
            try
            {
                var box = new Rectangle(widget.X, widget.Y, widget.Width, widget.Height);
                g.SetClip(box);
                if (widget.Background.Length > 0) { using var bg = new SolidBrush(ParseColor(widget.Background)); g.FillRectangle(bg, box); }
                if (WidgetLayout.IsGraph(widget.Kind)) { DrawGraph(g, widget, t, box); continue; }
                if (WidgetLayout.IsGauge(widget.Kind))
                {
                    DrawGauge(g, widget, t, box);
                    if (!widget.GaugeShowText) continue;
                }
                bool linear = widget.Kind is WidgetKind.HorizontalGauge or WidgetKind.VerticalGauge;
                var textColor = WidgetLayout.IsGauge(widget.Kind) && widget.GaugeTextColor.Length > 0 ? ParseColor(widget.GaugeTextColor)
                    : linear ? Color.White : ParseColor(widget.Foreground);
                using var ink = new SolidBrush(textColor);
                string text = WidgetLayout.Format(widget, t);
                var font = LogWidget.IsLog(widget.Source) && widget.LogAutoFit ? LogFont(g, widget, text, box)
                    : FontFor(widget.FontFamily, widget.FontSize, widget.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                using var format = new StringFormat { Alignment = widget.Alignment switch { WidgetAlignment.Left => StringAlignment.Near, WidgetAlignment.Right => StringAlignment.Far, _ => StringAlignment.Center }, LineAlignment = LogWidget.IsLog(widget.Source) ? StringAlignment.Near : StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                if (linear)
                {
                    // A subtle shadow keeps the value readable on both the track and the filled portion.
                    using var shadow = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
                    var shifted = box; shifted.Offset(1, 1); g.DrawString(text, font, shadow, shifted, format);
                }
                g.DrawString(text, font, ink, box, format);
            }
            finally { g.Restore(state); }
        }
    }
    private void DrawGraph(Graphics g, LcdWidget widget, Telemetry telemetry, Rectangle box)
    {
        bool labels = widget.GraphShowLabels && box.Width >= 80 && box.Height >= 55;
        var area = new RectangleF(box.X + (labels ? 27 : 2), box.Y + (labels ? 20 : 2), Math.Max(1, box.Width - (labels ? 31 : 4)), Math.Max(1, box.Height - (labels ? 36 : 4)));
        using var ink = new SolidBrush(Color.White); using var muted = new SolidBrush(Color.FromArgb(180, 202, 220));
        using var track = new Pen(ParseColor(widget.GaugeTrackColor)); using var line = new Pen(ParseColor(widget.Foreground), widget.GraphLineWidth);
        using var dots = new SolidBrush(line.Color);
        var font = FontFor(widget.FontFamily, Math.Clamp(widget.FontSize, 8, 12), FontStyle.Regular, GraphicsUnit.Pixel);
        var tiny = FontFor(widget.FontFamily, 8, FontStyle.Regular, GraphicsUnit.Pixel);
        using var textFormat = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        if (labels) g.DrawString(WidgetLayout.Format(widget, telemetry), font, ink, new RectangleF(box.X + 2, box.Y, box.Width - 4, 19), textFormat);
        var buckets = GraphProjection.Build(telemetry.History, widget.Source, telemetry.Time, widget.GraphMinutes, (int)Math.Min(240, area.Width), widget.Port);
        if (buckets.Count == 0)
        {
            g.DrawString("Waiting for history", tiny, muted, area, textFormat); return;
        }
        double low = widget.GaugeMinimum, high = widget.GaugeMaximum;
        if (widget.GraphAutoScale)
        {
            low = buckets.Min(p => p.Minimum!.Value); high = buckets.Max(p => p.Maximum!.Value);
            if (widget.Kind == WidgetKind.BarGraph) { low = Math.Min(0, low); high = Math.Max(0, high); }
            double pad = Math.Max(.5, (high - low) * .08); low -= pad; high += pad;
            if (widget.Kind == WidgetKind.BarGraph && low > -.51) low = 0;
        }
        float Y(double number) => area.Bottom - (float)Math.Clamp((number - low) / (high - low), 0, 1) * area.Height;
        if (labels)
        {
            g.DrawLine(track, area.Left, area.Top, area.Right, area.Top); g.DrawLine(track, area.Left, area.Bottom, area.Right, area.Bottom);
            g.DrawString(high.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), tiny, muted, box.X, area.Top);
            g.DrawString(low.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), tiny, muted, box.X, area.Bottom - 10);
            g.DrawString($"-{widget.GraphMinutes}m", tiny, muted, area.Left, area.Bottom + 2);
            using var right = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString("now", tiny, muted, new RectangleF(area.Right - 25, area.Bottom + 2, 25, 13), right);
        }
        var plotState = g.Save();
        try
        {
            g.SetClip(area, CombineMode.Intersect); PointF? previous = null;
            foreach (var bucket in buckets)
            {
                float x = area.Left + (float)bucket.X * area.Width; var point = new PointF(x, Y(bucket.Last!.Value));
                if (widget.Kind == WidgetKind.BarGraph) g.DrawLine(line, x, Y(0), x, point.Y);
                else
                {
                    if (!bucket.BreakBefore && previous.HasValue) g.DrawLine(line, previous.Value, point);
                    g.DrawLine(line, x, Y(bucket.Minimum!.Value), x, Y(bucket.Maximum!.Value));
                    g.FillEllipse(dots, x - 1, point.Y - 1, 2, 2);
                }
                previous = point;
            }
        }
        finally { g.Restore(plotState); }
    }
    private static void DrawGauge(Graphics g, LcdWidget widget, Telemetry t, RectangleF box)
    {
        double? fraction = WidgetLayout.GaugeFraction(widget, t);
        if (widget.Kind is WidgetKind.HorizontalGauge or WidgetKind.VerticalGauge)
        {
            var area = RectangleF.Inflate(box, -1, -1);
            using var track = new SolidBrush(ParseColor(widget.GaugeTrackColor));
            using var fill = new SolidBrush(ParseColor(widget.Foreground));
            g.FillRectangle(track, area);
            if (fraction is > 0)
            {
                var filled = area;
                if (widget.Kind == WidgetKind.HorizontalGauge) filled.Width *= (float)fraction.Value;
                else { filled.Height *= (float)fraction.Value; filled.Y = area.Bottom - filled.Height; }
                g.FillRectangle(fill, filled);
            }
            return;
        }
        float size = Math.Min(box.Width, box.Height) - widget.GaugeThickness - 2;
        var arc = new RectangleF(box.X + (box.Width - size) / 2, box.Y + (box.Height - size) / 2, size, size);
        float start = widget.GaugeStartAngle, sweep = widget.GaugeSweepAngle;
        if (widget.Kind == WidgetKind.RadialRing) { start = -90; sweep = 360; }
        else if (widget.Kind == WidgetKind.RadialSemicircle) { start = 180; sweep = 180; }
        using var trackPen = new Pen(ParseColor(widget.GaugeTrackColor), widget.GaugeThickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fillPen = new Pen(ParseColor(widget.Foreground), widget.GaugeThickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (widget.Kind == WidgetKind.RadialSegments)
        {
            trackPen.StartCap = trackPen.EndCap = fillPen.StartCap = fillPen.EndCap = LineCap.Flat;
            float step = sweep / widget.GaugeSegments, part = step * .72f;
            for (int i = 0; i < widget.GaugeSegments; i++)
            {
                float angle = start + i * step + (step - part) / 2;
                g.DrawArc(trackPen, arc, angle, part);
                float amount = (float)Math.Clamp((fraction ?? 0) * widget.GaugeSegments - i, 0, 1);
                if (amount > 0) g.DrawArc(fillPen, arc, angle, part * amount);
            }
        }
        else
        {
            g.DrawArc(trackPen, arc, start, sweep);
            if (widget.Kind == WidgetKind.RadialNeedle)
            {
                // Missing telemetry has no needle; it is not misrepresented as the minimum value.
                if (fraction.HasValue)
                {
                    double angle = (start + fraction.Value * sweep) * Math.PI / 180;
                    var center = new PointF(arc.X + arc.Width / 2, arc.Y + arc.Height / 2);
                    var tip = new PointF(center.X + (float)Math.Cos(angle) * size * .43f, center.Y + (float)Math.Sin(angle) * size * .43f);
                    using var needle = new Pen(ParseColor(widget.Foreground), Math.Clamp(widget.GaugeThickness / 3, 1, 5)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(needle, center, tip);
                }
            }
            else if (fraction is > 0) g.DrawArc(fillPen, arc, start, (float)fraction.Value * sweep);
        }
    }
    private static void DrawFit(Graphics g, Image image, int w, int h)
    {
        double scale = Math.Min((double)w / image.Width, (double)h / image.Height);
        int dw = (int)(image.Width * scale), dh = (int)(image.Height * scale);
        g.DrawImage(image, (w - dw) / 2, (h - dh) / 2, dw, dh);
    }
    private void DrawMetrics(Graphics g, Telemetry t, int w, int h, bool overlay)
    {
        if (overlay) { using var bg = new SolidBrush(Color.FromArgb(190, 10, 17, 29)); g.FillRectangle(bg, 0, 0, w, h); }
        if (h >= w)
        {
            float s = w / 480f;
            using var ring = new Pen(Color.FromArgb(38, 218, 179), Math.Max(2, 4 * s));
            using var faint = new Pen(Color.FromArgb(36, 58, 73), Math.Max(1, 2 * s));
            var title = FontFor("Segoe UI", Math.Max(6, 22 * s), FontStyle.Bold);
            var number = FontFor("Segoe UI", Math.Max(12, 68 * s), FontStyle.Bold);
            var detail = FontFor("Segoe UI", Math.Max(6, 30 * s));
            var clock = FontFor("Segoe UI", Math.Max(6, 27 * s), FontStyle.Bold);
            using var ink = new SolidBrush(Color.FromArgb(151, 172, 191));
            using var teal = new SolidBrush(Color.FromArgb(38, 218, 179));
            float cy = h / 2f;
            void Center(string text, Font font, Brush brush, float y)
            { float textWidth = g.MeasureString(text, font).Width; g.DrawString(text, font, brush, (w - textWidth) / 2, y); }
            g.DrawEllipse(faint, 24 * s, cy - w / 2f + 24 * s, w - 48 * s, w - 48 * s);
            g.DrawArc(ring, 24 * s, cy - w / 2f + 24 * s, w - 48 * s, w - 48 * s, -90, (float)Math.Clamp(t.CpuTemperature ?? 0, 0, 100) * 3.6f);
            Center("THERMALDESK", title, teal, cy - 150 * s);
            Center("CPU TEMPERATURE", title, ink, cy - 100 * s);
            Center(FormatTemperature(t.CpuTemperature), number, Brushes.White, cy - 73 * s);
            Center("GPU  " + FormatTemperature(t.GpuTemperature), detail, ink, cy + 44 * s);
            Center($"MEMORY  {t.MemoryUsedPercent:0}%", title, ink, cy + 86 * s);
            Center(DateTime.Now.ToString("HH:mm:ss"), clock, teal, cy + 125 * s);
            return;
        }
        float scale = Math.Min(w / 480f, h / 128f);
        var small = FontFor("Segoe UI", Math.Max(8, 10 * scale));
        var large = FontFor("Segoe UI", Math.Max(12, 24 * scale), FontStyle.Bold);
        var heading = FontFor("Segoe UI", Math.Max(8, 9 * scale), FontStyle.Bold);
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
            g.DrawString(i == 2 ? (values[i].HasValue ? $"{values[i]:0}%" : "—") : FormatTemperature(values[i]), large, Brushes.White, x, y + 16 * scale);
            using var track = new SolidBrush(Color.FromArgb(33, 49, 65));
            g.FillRectangle(track, x, y + 59 * scale, cell - 28, 3);
            if (values[i].HasValue) g.FillRectangle(accent, x, y + 59 * scale, (float)Math.Clamp(values[i]!.Value / 100, 0, 1) * (cell - 28), 3);
        }
        g.DrawString(DateTime.Now.ToString("HH:mm:ss"), small, muted, 16, h - 23);
    }
    public static byte[] Jpeg(Bitmap bitmap)
    {
        using var data = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        bitmap.Save(data, JpegCodec, parameters); return data.ToArray();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        media?.Dispose(); media = null; foreach (var font in fonts.Values) font.Dispose(); fonts.Clear(); jpegBuffer.Dispose(); jpegParameters.Dispose();
    }
}
