using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class PresentationChecks
{
    internal static int Run(string directory) => Task.Run(() => RunAsync(directory)).GetAwaiter().GetResult();
    private static async Task<int> RunAsync(string directory)
    {
        int count = 0;
        void Check(bool value, string label) { count++; if (!value) throw new InvalidOperationException("Presentation: " + label); }
        byte[] ImageBytes(int width = 320, int height = 180)
        {
            using var bitmap = new Bitmap(width, height); using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.FromArgb(12, 170, 160)); using var brush = new SolidBrush(Color.White);
            g.FillRectangle(brush, width / 4, height / 4, width / 2, height / 2);
            using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Jpeg); return stream.ToArray();
        }
        async Task Until(Func<bool> condition)
        {
            var timeout = Stopwatch.StartNew(); while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
            Check(condition(), "mock source delivered / disposed within deadline");
        }
        var widget = new LcdWidget { Name = "Webcam", Kind = WidgetKind.OnlineImage, OnlineUrl = "http://camera.test/snapshot.jpg", X = 4, Y = 4, Width = 220, Height = 120 };
        var settings = new Settings(); settings.Panel.Enabled = settings.Panel.UseCustomLayout = true; settings.Panel.Widgets = [widget];
        var bytes = ImageBytes();
        var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        handler.Type = "IMAGE/JPEG";
        using (var cache = new OnlineMediaCache(handler))
        using (var cancel = new CancellationTokenSource())
        {
            var task = cache.Run(() => settings, cancel.Token);
            try
            {
            await Until(() => cache.CachedFrames == 1); Check(handler.Requests == 1, "one shared snapshot request");
            long revision = cache.Revision(widget); Check(revision > 0, "online cache changes the visual key");
            using var renderer = new FrameRenderer(); var t = new Telemetry(DateTimeOffset.Now, 45, 40, 10, 20, 30);
            using var image = renderer.Render(t, settings.Panel, 480, 128, cache); image.Save(Path.Combine(directory, "webcam-snapshot-widget.png"));
            Check(image.GetPixel(110, 60).R > 200, "cached webcam pixels are rendered");
            widget.ImageFit = ImageFit.Cover; widget.CornerRadius = 16;
            using var cover = renderer.Render(t, settings.Panel, 480, 128, cache); cover.Save(Path.Combine(directory, "webcam-cover-widget.png"));
            }
            finally { cancel.Cancel(); await task; }
            Check(cache.CachedFrames == 0, "stop releases retained bitmaps");
        }
        // Feed arbitrary one-byte chunks so JPEG markers may cross any read.
        widget.OnlineMode = OnlineMediaMode.Mjpeg;
        var wire = "--frame\r\nContent-Type: image/jpeg\r\n\r\n"u8.ToArray().Concat(bytes).Concat("\r\n--frame--\r\n"u8.ToArray()).ToArray();
        var mjpeg = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkStream(wire)) });
        mjpeg.Type = "multipart/x-mixed-replace";
        using (var cache = new OnlineMediaCache(mjpeg))
        using (var cancel = new CancellationTokenSource())
        {
            var task = cache.Run(() => settings, cancel.Token);
            try { await Until(() => cache.CachedFrames == 1); Check(mjpeg.Requests == 1, "MJPEG does not reconnect or buffer unbounded frames"); }
            finally { cancel.Cancel(); await task; }
        }
        widget.OnlineMode = OnlineMediaMode.Snapshot;
        using (var cache = new OnlineMediaCache(new Handler(() => throw new HttpRequestException("private URL must not reach status"))))
        using (var cancel = new CancellationTokenSource())
        {
            var task = cache.Run(() => settings, cancel.Token);
            try
            {
            await Until(() => cache.Revision(widget) >= 2);
            using var image = new Bitmap(240, 128); using var g = Graphics.FromImage(image);
            string status = cache.Draw(g, widget, new(0, 0, 240, 128), DateTimeOffset.Now);
            Check(!status.Contains("private") && status.Contains("Offline"), "error UI excludes URL / exception text");
            }
            finally { cancel.Cancel(); await task; }
        }
        using (var cache = new OnlineMediaCache())
        {
            bool rejected = false; try { cache.AcceptFrame("oversize", new byte[OnlineMediaPolicy.MaxFrameBytes + 1]); } catch (IOException) { rejected = true; }
            Check(rejected, "compressed frame byte cap");
            rejected = false; try { cache.AcceptFrame("oversize-pixels", ImageBytes(4097, 1)); } catch (IOException) { rejected = true; }
            Check(rejected && cache.CachedFrames == 0, "decoded dimensions checked before keeping frame");
            for (int i = 0; i < 100; i++) cache.AcceptFrame("single", bytes);
            Check(cache.CachedFrames == 1, "100 replacements keep only one current bitmap");
        }
        var s = new Settings { AllowMonitoringOnly = true, LastSessionWasLive = true };
        Check(StartupManager.ShouldStartLive(s, [], false), "last authorized Live state resumes");
        s.ResumeLastProfileOnLaunch = false; Check(!StartupManager.ShouldStartLive(s, [], false), "explicit resume opt-out wins");
        s.ResumeLastProfileOnLaunch = true;
        Check(StartupManager.ShouldStartLive(s, ["--minimized"], false), "explicit resume works when minimized");
        Check(!StartupManager.ShouldStartLive(s, [], true) && !StartupManager.ShouldStartLive(s, ["--preview"], false) && !StartupManager.ShouldStartLive(s, [], false, true), "Shift / Preview / recovery never auto-start hardware");
        var first = new Settings { AllowMonitoringOnly = true }; StartupManager.RememberSuccessfulLive(first);
        Check(first.LastSessionWasLive && first.ResumeLastProfileOnLaunch == true && StartupManager.ShouldStartLive(first, [], false), "first authorized Live start is persisted for next launch");
        first.ResumeLastProfileOnLaunch = false; StartupManager.RememberSuccessfulLive(first);
        Check(first.ResumeLastProfileOnLaunch == false, "later Live starts preserve an explicit resume opt-out");
        using (var renderer = new FrameRenderer())
        {
            var pages = new DisplaySettings { UseCustomLayout = true, RotateWidgetGroups = true, Widgets =
                [new() { Group = 1, X = 0, Y = 0, Width = 120, Height = 60, Background = "FF0000", Source = WidgetSource.Text, TextTemplate = "", CornerRadius = 0 },
                 new() { Group = 2, X = 0, Y = 0, Width = 120, Height = 60, Background = "0000FF", Source = WidgetSource.Text, TextTemplate = "", CornerRadius = 0 }] };
            var t = new Telemetry(DateTimeOffset.UnixEpoch, 40, 40, 5, 5, 25);
            using var red = renderer.Render(t, pages, 480, 128); using var blue = renderer.Render(t with { Time = t.Time.AddSeconds(5) }, pages, 480, 128);
            Check(red.GetPixel(20, 20).R == 255 && blue.GetPixel(20, 20).B == 255, "page switching changes actual rendered pixels");
            Check(FrameRenderer.VisualKey(t, pages) != FrameRenderer.VisualKey(t with { Time = t.Time.AddSeconds(5) }, pages), "page boundary invalidates unchanged-value JPEG cache");
            red.Save(Path.Combine(directory, "rotating-page-1.png")); blue.Save(Path.Combine(directory, "rotating-page-2.png"));
        }
        return count;
    }
    private sealed class Handler(Func<HttpResponseMessage> create) : HttpMessageHandler
    {
        public int Requests;
        public string Type = "image/jpeg";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Requests); token.ThrowIfCancellationRequested(); var response = create(); response.Content.Headers.ContentType = new MediaTypeHeaderValue(Type); return Task.FromResult(response); }
    }
    private sealed class ChunkStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }
}
