using System.ComponentModel;
using System.Drawing.Imaging;
using LibreHardwareMonitor.Hardware;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// Offline rendering/projection checks only. Do not construct Monitoring or open USB here.
internal static class NewWidgetChecks
{
    public static int Run(string directory)
    {
        int checks = 0;
        void Check(bool passed, string message) { checks++; if (!passed) throw new InvalidOperationException("Extended widgets: " + message); }
        Directory.CreateDirectory(directory);
        var time = new DateTimeOffset(2026, 11, 12, 14, 30, 0, TimeSpan.Zero);
        var t = new Telemetry(time, 53, 42, 35, 50, 41)
        {
            GpuVramUsedGiB = 8.5, GpuVramTotalGiB = 16, GpuVramUsedPercent = 53.125,
            CpuClockAverageMhz = 4850, CpuClockMaximumMhz = 5250, GpuCoreClockMhz = 2505, GpuMemoryClockMhz = 11202
        };
        Check(Monitoring.NormalizeReading("GPU Memory Used", SensorType.SmallData, 8704)?.Value == 8.5, "Pinned LHM MiB converts once to GiB");
        Check(Monitoring.NormalizeReading("GPU Memory Total", SensorType.SmallData, 16384)?.Value == 16, "Pinned LHM capacity uses binary GiB");
        Check(Monitoring.NormalizeReading("GPU Memory Used", SensorType.Data, 8.5f)?.Value == 8.5, "Data GiB not divided a second time");
        Check(Monitoring.NormalizeReading("GPU Core", SensorType.Clock, 2505) is { Metric: SensorMetric.ClockMhz, Value: 2505 }, "Clock normalizer keeps MHz");
        Check(Monitoring.NormalizeReading("GPU Memory Used", SensorType.SmallData, null)?.Value is null, "Missing raw sensor stays null");
        Check(Monitoring.NormalizeReading("Unused", SensorType.Voltage, 1.2f) is null, "Unsupported sensor ignored");
        var adapterWidget = new LcdWidget();
        var adapter = new WidgetProperties(adapterWidget);
        foreach (var source in Enum.GetValues<WidgetSource>())
        {
            var converter = TypeDescriptor.GetProperties(adapter)[nameof(WidgetProperties.Source)]!.Converter;
            string label = converter.ConvertToString(source)!;
            Check((WidgetSource)converter.ConvertFromString(label)! == source, "Friendly source name round-trip: " + source);
            adapter.Source = source;
            Check(!WidgetLayout.Format(adapterWidget, t).Contains("{value}") || source == WidgetSource.Text, "Source has usable format: " + source);
        }
        adapter.Source = WidgetSource.GpuVramUsedGiB;
        Check(adapter.Maximum == 16 && adapter.Text == "VRAM {value} GiB" && WidgetLayout.Format(adapterWidget, t) == "VRAM 8.5 GiB", "VRAM source suggests capacity and binary units");
        adapter.Source = WidgetSource.CpuClockAverageMhz;
        Check(adapter.Maximum == 6000 && adapter.ValueFormat == "0" && WidgetLayout.Format(adapterWidget, t) == "CPU AVG 4850 MHz", "CPU clock source has MHz range and readable label");
        adapter.Source = WidgetSource.GpuMemoryClockMhz;
        Check(adapter.Maximum == 16000 && adapter.Text.EndsWith("MHz"), "GPU memory clock is explicitly MHz, not MT/s");
        adapter.TextColor = Color.White; adapter.TrackColor = Color.Navy; adapter.Segments = 30;
        Check(adapterWidget.GaugeTextColor == "FFFFFF" && adapterWidget.GaugeTrackColor == "000080" && adapterWidget.GaugeSegments == 30, "Gauge colors and segment count editable");
        adapter.TextColor = Color.Transparent; Check(adapterWidget.GaugeTextColor == "", "Automatic text color can be restored");
        using var renderer = new FrameRenderer();
        var kinds = Enum.GetValues<WidgetKind>().Where(WidgetLayout.IsGauge).ToArray();
        foreach (var kind in kinds)
        {
            var converter = TypeDescriptor.GetProperties(adapter)[nameof(WidgetProperties.Kind)]!.Converter;
            Check((WidgetKind)converter.ConvertFromString(converter.ConvertToString(kind)!)! == kind, "Friendly gauge name round-trip: " + kind);
            foreach (var size in new[] { (Width: 480, Height: 128), (Width: 480, Height: 480) })
            foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                var display = Display(kind); display.Rotation = rotation;
                WidgetLayout.Validate(display, size.Width, size.Height);
                using var frame = renderer.Render(t, display, size.Width, size.Height);
                Check(frame.Width == size.Width && frame.Height == size.Height, "Native frame resolution after rotation: " + kind + "/" + rotation);
                using var data = new MemoryStream(FrameRenderer.Jpeg(frame)); using var decoded = new Bitmap(data);
                Check(decoded.Width == size.Width && decoded.Height == size.Height, "JPEG frame dimensions: " + kind);
            }
            var settings = Display(kind);
            using var noSensor = renderer.Render(t with { GpuLoad = null }, settings, 128, 128);
            using var zero = renderer.Render(t with { GpuLoad = 0 }, settings, 128, 128);
            using var half = renderer.Render(t with { GpuLoad = 50 }, settings, 128, 128);
            using var full = renderer.Render(t with { GpuLoad = 100 }, settings, 128, 128);
            Check(AccentPixels(noSensor) == 0, "Missing sensor has no progress or needle: " + kind);
            Check(AccentPixels(half) > 0, "Mid-value is visibly rendered: " + kind);
            if (kind != WidgetKind.RadialNeedle)
            {
                Check(AccentPixels(zero) == 0, "Zero gauge has no fill: " + kind);
                Check(AccentPixels(full) > AccentPixels(half), "Full gauge visibly exceeds half: " + kind);
            }
            else Check(AccentPixels(zero) > 0, "Valid zero needle is distinct from missing telemetry");
            if (kind == WidgetKind.HorizontalGauge)
                Check(IsAccent(half.GetPixel(25, 60)) && !IsAccent(half.GetPixel(95, 60)), "Horizontal bar grows left-to-right");
            if (kind == WidgetKind.VerticalGauge)
                Check(!IsAccent(half.GetPixel(60, 25)) && IsAccent(half.GetPixel(60, 95)), "Vertical bar grows bottom-to-top");
            settings.Widgets[0].GaugeShowText = true; settings.Widgets[0].GaugeTextColor = "FFFFFF";
            using var textFrame = renderer.Render(t, settings, 128, 128);
            Check(WhitePixels(textFrame) > 10, "Independent visible gauge text: " + kind);
        }
        // Montage for human visual QA; rows are unavailable / zero / half / full.
        using (var gallery = new Bitmap(kinds.Length * 160, 4 * 175 + 46))
        using (var g = Graphics.FromImage(gallery))
        using (var labelFont = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            g.Clear(Color.FromArgb(10, 17, 29));
            for (int column = 0; column < kinds.Length; column++)
            {
                g.DrawString(WidgetKindConverter.Label(kinds[column]), labelFont, Brushes.White, column * 160 + 8, 8);
                for (int row = 0; row < 4; row++)
                {
                    double? value = row == 0 ? null : (row - 1) * 50;
                    var display = Display(kinds[column]); display.Widgets[0].GaugeShowText = true; display.Widgets[0].GaugeTextColor = "FFFFFF";
                    using var cell = renderer.Render(t with { GpuLoad = value }, display, 128, 128);
                    int y = row * 175 + 35;
                    g.DrawImageUnscaled(cell, column * 160 + 12, y);
                    g.DrawString(value is null ? "Unavailable" : value + "%", labelFont, Brushes.LightGray, column * 160 + 20, y + 130);
                }
            }
            gallery.Save(Path.Combine(directory, "gauge-styles-contact-sheet.png"), ImageFormat.Png);
        }
        var demo = new DisplaySettings { UseCustomLayout = true, Widgets =
        [
            new() { Name = "VRAM", Source = WidgetSource.GpuVramUsedPercent, Kind = WidgetKind.HorizontalGauge, X = 12, Y = 12, Width = 220, Height = 44, FontSize = 22, TextTemplate = "VRAM {value}%", ValueFormat = "0", Foreground = "26DAB3", GaugeTextColor = "FFFFFF" },
            new() { Name = "CPU clock", Source = WidgetSource.CpuClockAverageMhz, X = 244, Y = 12, Width = 224, Height = 44, FontSize = 21, TextTemplate = "CPU {value} MHz", ValueFormat = "0" },
            new() { Name = "GPU clock", Source = WidgetSource.GpuCoreClockMhz, X = 12, Y = 68, Width = 220, Height = 44, FontSize = 21, TextTemplate = "GPU {value} MHz", ValueFormat = "0" },
            new() { Name = "VRAM used", Source = WidgetSource.GpuVramUsedGiB, X = 244, Y = 68, Width = 224, Height = 44, FontSize = 21, TextTemplate = "VRAM {value} GiB", ValueFormat = "0.0" }
        ] };
        WidgetLayout.Validate(demo, 480, 128);
        using (var frame = renderer.Render(t, demo, 480, 128)) frame.Save(Path.Combine(directory, "new-sources-landscape.png"), ImageFormat.Png);
        using (var editor = new WidgetDesignerForm("Offline extended widget check", demo, 480, 128, () => t)) editor.RenderForTest(Path.Combine(directory, "extended-widget-designer.png"));
        return checks;
    }
    private static DisplaySettings Display(WidgetKind kind) => new()
    {
        UseCustomLayout = true,
        Widgets = [new() { Kind = kind, Source = WidgetSource.GpuLoad, X = 12, Y = 12, Width = 104, Height = 104, GaugeThickness = 10, Foreground = "26DAB3", GaugeShowText = false, FontSize = 20, TextTemplate = "{value}%", ValueFormat = "0" }]
    };
    private static bool IsAccent(Color color) => color.G > color.R + 50 && color.B > color.R + 30 && color.G > 140;
    private static int AccentPixels(Bitmap image) => CountPixels(image, IsAccent);
    private static int WhitePixels(Bitmap image) => CountPixels(image, c => c.R > 210 && c.G > 210 && c.B > 210);
    private static int CountPixels(Bitmap image, Func<Color, bool> test)
    {
        int result = 0;
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) if (test(image.GetPixel(x, y))) result++;
        return result;
    }
}
