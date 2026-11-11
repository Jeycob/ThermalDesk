using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class WidgetChecks
{
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var t = new Telemetry(DateTimeOffset.Now, 62.5, 57, 38, 68, 41) { GpuHotSpotTemperature = 75, GpuMemoryTemperature = 70, Fans = [new(1, 1250, 60), new(2, 1400, 65)] };
        int count = 0;
        void Check(bool value, string message) { count++; if (!value) throw new InvalidOperationException(message); }
        try
        {
            foreach (var size in new[] { (480, 128), (480, 480) })
            foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                var s = new DisplaySettings { UseCustomLayout = true, Rotation = rotation };
                var canvas = WidgetLayout.Canvas(s, size.Item1, size.Item2); s.Widgets = WidgetLayout.Defaults(canvas.Width, canvas.Height);
                WidgetLayout.Validate(s, size.Item1, size.Item2);
                using var renderer = new FrameRenderer(); using var image = renderer.Render(t, s, size.Item1, size.Item2);
                Check(image.Width == size.Item1 && image.Height == size.Item2, "Rotation must preserve physical output resolution.");
                image.Save(Path.Combine(directory, $"widgets-{size.Item1}x{size.Item2}-{rotation}.png"), ImageFormat.Png);
            }
            var profile = new Settings();
            profile.Panel.UseCustomLayout = profile.Aio.UseCustomLayout = true;
            profile.Panel.Widgets = WidgetLayout.Defaults(480, 128); profile.Aio.Widgets = WidgetLayout.Defaults(480, 480);
            string path = Path.Combine(directory, "widget-profile.json"); SettingsStore.Save(path, profile); var restored = SettingsStore.Load(path);
            Check(restored.Aio.Widgets.Count == 6 && restored.Panel.Widgets.Count == 5, "Profile must round-trip both independent layouts.");
            using (var form = new WidgetDesignerForm("TOUGHLIQUID AX · 480 × 480", restored.Aio, 480, 480, () => t))
            { form.RenderForTest(Path.Combine(directory, "designer-round.png")); Check(form.Result.Widgets.Count == 6, "Designer must keep widgets."); count += form.ExerciseForTest(); Check(restored.Aio.Widgets.Count == 6, "Editing a dialog must not mutate its input profile."); }
            using (var form = new WidgetDesignerForm("3.9-inch LCD · 480 × 128", restored.Panel, 480, 128, () => t))
            { form.RenderForTest(Path.Combine(directory, "designer-wide.png")); Check(form.Result.Widgets.Count == 5, "Wide designer must keep widgets."); count += form.ExerciseForTest(); }
            using (var form = new MainForm(restored, path)) form.RenderPreview(Path.Combine(directory, "main-widgets.png"));
            using var process = Process.GetCurrentProcess();
            using var repeated = new FrameRenderer();
            using (var warmup = repeated.Render(t, restored.Aio, 480, 480)) { }
            int before = GetGuiResources(process.Handle, 0);
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) { using var image = repeated.Render(t with { Time = t.Time.AddSeconds(i) }, restored.Aio, 480, 480); }
            clock.Stop(); int after = GetGuiResources(process.Handle, 0);
            Check(after - before <= 2, "GDI resource count grew during 200 repeated widget renders.");
            File.WriteAllText(Path.Combine(directory, "widget-checks.json"), JsonSerializer.Serialize(new { passed = true, checks = count, hardwareAccess = false, renders = 200, elapsedMilliseconds = clock.Elapsed.TotalMilliseconds, gdiBefore = before, gdiAfter = after }, Settings.JsonOptions));
            return 0;
        }
        catch (Exception e)
        { File.WriteAllText(Path.Combine(directory, "widget-checks.json"), JsonSerializer.Serialize(new { passed = false, checks = count, hardwareAccess = false, error = e.ToString() }, Settings.JsonOptions)); return 1; }
    }
}
