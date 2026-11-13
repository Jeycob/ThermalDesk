using System.Buffers;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Runtime.InteropServices;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// A bounded, latest-frame-only cache. No browser, subprocess, codec installation,
// frame queue, hardware access, automatic discovery or network use in Preview.
internal sealed class OnlineMediaCache : IDisposable
{
    private sealed class Frame : IDisposable
    {
        public Bitmap? Image;
        public long Revision;
        public DateTimeOffset Time;
        public string Status = "";
        public void Dispose() => Image?.Dispose();
    }
    private sealed record Source(string Key, string Url, OnlineMediaMode Mode, int Seconds, int Fps);
    private sealed record Worker(Source Source, CancellationTokenSource Stop, Task Task);
    private readonly object gate = new();
    private readonly Dictionary<string, Frame> frames = new(StringComparer.Ordinal);
    private readonly Lazy<HttpClient> http;
    private bool disposed;
    internal OnlineMediaCache(HttpMessageHandler? handler = null)
    {
        http = new(() => new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            MaxConnectionsPerServer = OnlineMediaPolicy.MaxSources,
            ConnectTimeout = TimeSpan.FromSeconds(4), PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None
        }, true) { Timeout = Timeout.InfiniteTimeSpan });
    }
    public long Revision(LcdWidget widget)
    {
        if (widget.OnlineUrl.Length == 0) return 0;
        string key = OnlineMediaPolicy.Key(widget);
        lock (gate) return frames.TryGetValue(key, out var frame) ? frame.Revision : 0;
    }
    public string Draw(Graphics graphics, LcdWidget widget, Rectangle bounds, DateTimeOffset now)
    {
        if (widget.OnlineUrl.Length == 0) return "Set a direct image URL";
        string key = OnlineMediaPolicy.Key(widget);
        lock (gate)
        {
            if (!frames.TryGetValue(key, out var frame)) return "Online source · Live only";
            if (frame.Image is null) return frame.Status;
            var image = frame.Image;
            double scale = widget.ImageFit == ImageFit.Cover ? Math.Max((double)bounds.Width / image.Width, (double)bounds.Height / image.Height)
                : Math.Min((double)bounds.Width / image.Width, (double)bounds.Height / image.Height);
            int width = Math.Max(1, (int)Math.Round(image.Width * scale)), height = Math.Max(1, (int)Math.Round(image.Height * scale));
            graphics.DrawImage(image, new Rectangle(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height));
            var age = now - frame.Time;
            return frame.Status != "Connected" || age.TotalSeconds > Math.Max(15, widget.OnlineRefreshSeconds * 3)
                ? "LAST FRAME · " + Math.Max(0, (int)age.TotalSeconds) + " s" : "";
        }
    }
    private void Status(string key, string status, bool initialOnly = false)
    {
        lock (gate)
        {
            if (disposed) return;
            if (!frames.TryGetValue(key, out var frame)) frames[key] = frame = new();
            if (initialOnly && frame.Image is not null) return;
            if (frame.Status != status) { frame.Status = status; frame.Revision++; }
        }
    }
    internal void AcceptFrame(string key, byte[] bytes)
    {
        if (bytes.Length is < 4 or > OnlineMediaPolicy.MaxFrameBytes) throw new IOException("Image exceeds the 4 MiB frame limit.");
        if (!(bytes[0] == 0xff && bytes[1] == 0xd8) && !(bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
            throw new IOException("Only JPEG and PNG frames are supported.");
        using var stream = new MemoryStream(bytes, false);
        using var image = Image.FromStream(stream, false, true);
        if (image.Width > 4096 || image.Height > 4096 || (long)image.Width * image.Height > OnlineMediaPolicy.MaxDecodedPixels)
            throw new IOException("Online image exceeds the 4 megapixel limit.");
        float ratio = Math.Min(1, 480f / Math.Max(image.Width, image.Height));
        var bitmap = new Bitmap(Math.Max(1, (int)(image.Width * ratio)), Math.Max(1, (int)(image.Height * ratio)));
        try { using var g = Graphics.FromImage(bitmap); g.InterpolationMode = InterpolationMode.Bilinear; g.DrawImage(image, 0, 0, bitmap.Width, bitmap.Height); }
        catch { bitmap.Dispose(); throw; }
        lock (gate)
        {
            if (disposed) { bitmap.Dispose(); return; }
            if (!frames.TryGetValue(key, out var frame)) frames[key] = frame = new();
            var previous = frame.Image; frame.Image = bitmap; frame.Time = DateTimeOffset.UtcNow;
            frame.Status = "Connected"; frame.Revision++; previous?.Dispose();
        }
    }
    public async Task Run(Func<Settings> settings, CancellationToken token)
    {
        var workers = new Dictionary<string, Worker>(StringComparer.Ordinal);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var requested = OnlineMediaPolicy.Sources(settings()).GroupBy(OnlineMediaPolicy.Key)
                    .Take(OnlineMediaPolicy.MaxSources).Select(g => new Source(g.Key, g.First().OnlineUrl,
                        g.First().OnlineMode, g.Min(w => w.OnlineRefreshSeconds), g.Max(w => w.OnlineMaxFps)))
                    .ToDictionary(s => s.Key, StringComparer.Ordinal);
                foreach (var key in workers.Keys.Where(k => !requested.TryGetValue(k, out var source) || source != workers[k].Source).ToArray())
                {
                    var worker = workers[key]; worker.Stop.Cancel(); await worker.Task; worker.Stop.Dispose(); workers.Remove(key);
                    lock (gate) { if (frames.Remove(key, out var frame)) frame.Dispose(); }
                }
                foreach (var source in requested.Values.Where(s => !workers.ContainsKey(s.Key)))
                {
                    var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                    workers[source.Key] = new(source, stop, Fetch(source, stop.Token));
                }
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            foreach (var worker in workers.Values) worker.Stop.Cancel();
            await Task.WhenAll(workers.Values.Select(w => w.Task));
            foreach (var worker in workers.Values) worker.Stop.Dispose();
            Clear();
        }
    }
    private async Task Fetch(Source source, CancellationToken token)
    {
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                Status(source.Key, "Connecting", initialOnly: true);
                if (source.Mode == OnlineMediaMode.Snapshot) await Snapshot(source, token);
                else await Mjpeg(source, token);
                failures = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException or OperationCanceledException or ExternalException or OutOfMemoryException)
            {
                // No URL/query strings in status; they may contain private tokens.
                failures = Math.Min(failures + 1, 5); Status(source.Key, "Offline · " + error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(failures == 0 ? source.Seconds : Math.Max(source.Seconds, Math.Min(60, 1 << failures))), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
    private async Task<HttpResponseMessage> Open(Source source, CancellationToken token)
    {
        var uri = OnlineMediaPolicy.Parse(source.Url);
        var response = await http.Value.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        try
        {
            response.EnsureSuccessStatusCode();
            string? type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            bool supported = source.Mode == OnlineMediaMode.Mjpeg ? type == "multipart/x-mixed-replace" : type is "image/jpeg" or "image/png";
            if (!supported) throw new IOException("Use a direct JPEG/PNG snapshot or multipart MJPEG URL, not a webpage or redirect.");
            return response;
        }
        catch { response.Dispose(); throw; }
    }
    private async Task Snapshot(Source source, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await Open(source, timeout.Token);
        if (response.Content.Headers.ContentLength > OnlineMediaPolicy.MaxFrameBytes) throw new IOException("Frame too large.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bytes = new MemoryStream(); byte[] buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            int count; while ((count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)) > 0)
            { if (bytes.Length + count > OnlineMediaPolicy.MaxFrameBytes) throw new IOException("Frame too large."); bytes.Write(buffer, 0, count); }
            AcceptFrame(source.Key, bytes.ToArray());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    private async Task Mjpeg(Source source, CancellationToken token)
    {
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); headerTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await Open(source, headerTimeout.Token);
        headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        // Parser recognizes JPEG markers across arbitrary transport chunk boundaries.
        // It keeps one compressed frame, discards intermediary frames before decode.
        using var frame = new MemoryStream(65536); byte[] buffer = ArrayPool<byte>.Shared.Rent(16384);
        int previous = -1, scanned = 0; bool capturing = false; long decodedAt = 0;
        var rateWindow = Stopwatch.StartNew(); long windowBytes = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                int count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeout.Token);
                if (count == 0) throw new IOException("MJPEG stream ended.");
                windowBytes += count;
                double delay = windowBytes / (2 * 1024d * 1024) - rateWindow.Elapsed.TotalSeconds;
                if (delay > .001) await Task.Delay(TimeSpan.FromSeconds(delay), token);
                if (rateWindow.Elapsed.TotalSeconds >= 1) { rateWindow.Restart(); windowBytes = 0; }
                for (int i = 0; i < count; i++)
                {
                    byte value = buffer[i];
                    if (!capturing && previous == 0xff && value == 0xd8) { frame.SetLength(0); frame.WriteByte(0xff); capturing = true; scanned = 0; }
                    if (capturing)
                    {
                        if (frame.Length >= OnlineMediaPolicy.MaxFrameBytes) throw new IOException("MJPEG frame too large.");
                        frame.WriteByte(value);
                        if (previous == 0xff && value == 0xd9)
                        {
                            if (decodedAt == 0 || Stopwatch.GetElapsedTime(decodedAt).TotalSeconds >= 1d / source.Fps)
                            { AcceptFrame(source.Key, frame.ToArray()); decodedAt = Stopwatch.GetTimestamp(); }
                            frame.SetLength(0); capturing = false;
                        }
                    }
                    else if (++scanned > OnlineMediaPolicy.MaxFrameBytes) throw new IOException("No JPEG frame in MJPEG stream.");
                    previous = value;
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    internal int CachedFrames { get { lock (gate) return frames.Values.Count(f => f.Image is not null); } }
    internal void Clear() { lock (gate) { foreach (var frame in frames.Values) frame.Dispose(); frames.Clear(); } }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; } if (http.IsValueCreated) http.Value.Dispose(); Clear(); }
}
