using System.Drawing.Imaging;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class LogAndPerformanceChecks
{
    internal static int Run(string directory)
    {
        int count = 0;
        void Check(bool condition, string name) { count++; if (!condition) throw new InvalidOperationException("Log/performance: " + name); }
        string root = Directory.CreateTempSubdirectory("thermaldesk-log-ui-").FullName;
        try
        {
            string path = Path.Combine(root, "sample.log"); File.WriteAllText(path, "First entry\nSecond entry\nThird entry");
            var widget = new LcdWidget { Source = WidgetSource.TopGpuProcessLog, Width = 440, Height = 90, X = 12, Y = 12,
                TextTemplate = "{value}", FontFamily = "Consolas", FontSize = 14, Bold = false, Alignment = WidgetAlignment.Left };
            var settings = new Settings(); settings.Panel.Enabled = settings.Panel.UseCustomLayout = true; settings.Panel.Widgets = [widget];
            var t = new Telemetry(DateTimeOffset.Now, 50, 40, 10, 20, 40) { TopGpuProcess = new(7, "test-process", 80) { StartedTicks = 123 } };
            int discoveries = 0; var sampler = new ProcessLogSampler(_ => { discoveries++; return path; });
            var first = sampler.Read(settings, t);
            for (int i = 0; i < 20; i++) sampler.Read(settings, t);
            Check(discoveries == 1 && sampler.FileReads == 1, "discovery is cached and unchanged file not reopened");
            File.AppendAllText(path, "\nFourth entry");
            var changed = sampler.Read(settings, t);
            Check(sampler.FileReads == 2 && changed[LogWidget.Key(widget)].Text.EndsWith("Fourth entry"), "append invalidates cached tail");
            File.WriteAllText(path, "Truncated");
            Check(sampler.Read(settings, t)[LogWidget.Key(widget)].Text == "Truncated", "truncation replaces old tail");
            string replacement = Path.Combine(root, "rotated.tmp"); File.WriteAllText(replacement, "Rotation replacement"); File.Move(replacement, path, true);
            Check(sampler.Read(settings, t)[LogWidget.Key(widget)].Text == "Rotation replacement", "file replacement / rotation picked up without holding a file handle");
            File.Delete(path);
            Check(sampler.Read(settings, t)[LogWidget.Key(widget)].Text.Length == 0, "deleted log does not keep displaying stale text");
            File.WriteAllText(path, "First entry\nSecond entry\nThird entry\nFourth entry");
            sampler.Read(settings, t with { TopGpuProcess = t.TopGpuProcess with { StartedTicks = 456 } });
            Check(discoveries == 2, "PID reuse invalidates automatic candidate");
            Check(ProcessLogSampler.FindCandidate([root]) == path, "bounded discovery finds a local log");
            string docs = Path.Combine(root, "README.txt"); File.WriteAllText(docs, "Not a log"); File.SetLastWriteTimeUtc(docs, DateTime.UtcNow.AddDays(1));
            Check(ProcessLogSampler.FindCandidate([root]) == path, "automatic discovery does not mistake README for a log");
            string backend = Directory.CreateDirectory(Path.Combine(root, "backend")).FullName;
            string frontend = Directory.CreateDirectory(Path.Combine(root, "frontend")).FullName;
            string backendLog = Path.Combine(backend, "server.log"), frontendLog = Path.Combine(frontend, "newest.log");
            File.WriteAllText(backendLog, "eval time = 1000 ms / 32 tokens (31.25 ms per token, 32.0 tokens per second)");
            File.WriteAllText(frontendLog, "unrelated updater log"); File.SetLastWriteTimeUtc(frontendLog, DateTime.UtcNow.AddHours(1));
            Check(ProcessLogSampler.FindPreferredCandidate("llama-server", [backend, "missing1", "missing2", frontend]) == backendLog,
                "authoritative LM backend timings take priority over newer frontend noise");
            string strataLog = Path.Combine(backend, "strata-iq3_s.log");
            File.WriteAllText(strataLog, "strata serve: prompt 10469 tokens = 10451 reused + 18 read in 133 ms (135.0 tok/s), 30 generated in 396 ms (75.7 tok/s), drafts accepted 15 of 17, 3 checkpoints");
            Check(ProcessLogSampler.FindPreferredCandidate("Strata", [backend, frontend]) == strataLog,
                "Strata native engine log wins over newer generic server/updater logs");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Check(ProcessLogSampler.CandidateDirectories("strata", root).First() == Path.Combine(local, "Strata", "install"),
                "Strata discovery checks the known native-engine location without recursive scans");
            var strataSampler = new ProcessLogSampler(_ => strataLog);
            var strataExcerpt = strataSampler.Read(settings, t with { TopGpuProcess = new(8, "strata", 80) { StartedTicks = 123 } })[LogWidget.Key(widget)];
            Check(strataExcerpt.IsAiLog && strataExcerpt.Metrics.GenerationTokensPerSecond == 75.7 && strataExcerpt.Metrics.PromptNewTokens == 18,
                "Strata top-process source discovers and parses actual AI metrics");
            File.WriteAllText(path, "[2026-11-12T19:44:53+01:00][DEBUG] eval time = 1000 ms / 32 tokens (31.25 ms per token, 32.0 tokens per second)");
            var inference = sampler.Read(settings, t)[LogWidget.Key(widget)];
            File.AppendAllText(path, "\n" + new string('x', BoundedLogTail.MaximumReadBytes * 2));
            var payload = sampler.Read(settings, t)[LogWidget.Key(widget)];
            Check(payload.Metrics.GenerationTokensPerSecond == 32 && payload.Metrics.GenerationReportedAt == inference.Metrics.GenerationReportedAt,
                "bounded append-only cache keeps inference metrics when a later payload exceeds the tail cap");
            File.WriteAllText(path, "new backend session");
            Check(sampler.Read(settings, t)[LogWidget.Key(widget)].Metrics.GenerationTokensPerSecond is null,
                "truncation clears old inference metrics instead of attributing them to a new session");
            File.WriteAllText(path, "generation: 41 tok/s\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));
            var untimed = sampler.Read(settings, t)[LogWidget.Key(widget)];
            File.AppendAllText(path, "new request without timing\n");
            var noisy = sampler.Read(settings, t)[LogWidget.Key(widget)];
            Check(noisy.Metrics.GenerationReportedAt == untimed.Metrics.GenerationReportedAt,
                "the same timestamp-free report offset does not become fresh because unrelated lines were appended");
            File.AppendAllText(path, "generation: 41 tok/s\n");
            var nextReport = sampler.Read(settings, t)[LogWidget.Key(widget)];
            Check(nextReport.Metrics.GenerationReportedAt > noisy.Metrics.GenerationReportedAt && nextReport.Metrics.GenerationReportOffset > noisy.Metrics.GenerationReportOffset,
                "a new identical-valued report at a new offset is recognized as genuinely newer");
            var telemetry = t with { LogExcerpts = changed };
            using var renderer = new FrameRenderer();
            using (var frame = renderer.Render(telemetry, settings.Panel, 480, 128))
            {
                frame.Save(Path.Combine(directory, "log-widget-landscape.png"), ImageFormat.Png);
                using var bytes = new MemoryStream(renderer.Encode(frame)); using var decoded = new Bitmap(bytes);
                Check(decoded.Width == 480 && decoded.Height == 128, "cached encoder keeps native LCD dimensions");
                Check(renderer.Encode(frame).Length > 100, "encoder stream reusable");
            }
            string? key = FrameRenderer.VisualKey(telemetry, settings.Panel);
            Check(key == FrameRenderer.VisualKey(telemetry with { Time = telemetry.Time.AddSeconds(1) }, settings.Panel), "non-clock dashboard key ignores sample timestamps");
            Check(key != FrameRenderer.VisualKey(t with { LogExcerpts = first }, settings.Panel), "changed log text invalidates cached frame");
            widget.Source = WidgetSource.GpuLoad; widget.Kind = WidgetKind.RadialGauge; widget.TextTemplate = "{value}"; widget.ValueFormat = "0";
            Check(FrameRenderer.VisualKey(t, settings.Panel) != FrameRenderer.VisualKey(t with { GpuLoad = 20.1 }, settings.Panel), "fractional gauge changes not hidden by rounded text");
            settings.Panel.Content = DisplayContent.Screen;
            Check(FrameRenderer.VisualKey(t, settings.Panel) is null, "screen capture is never frozen by dashboard cache");
            settings.Panel.Content = DisplayContent.Image;
            Check(FrameRenderer.VisualKey(t, settings.Panel) is null, "animated media never frozen by dashboard cache");
            foreach (var source in new[] { WidgetSource.TextFileLog, WidgetSource.TopCpuProcessLog, WidgetSource.TopGpuProcessLog })
            {
                var adapter = new WidgetProperties(widget); adapter.Source = source;
                Check(widget.Kind == WidgetKind.Text && widget.TextTemplate == "{value}", "selecting log source uses Text: " + source);
            }
            settings.Panel.Enabled = false;
            Check(sampler.Read(settings, t).Count == 0, "disabling log display drops sample data");
            var engine = new Engine(_ => throw new Exception("No USB allowed"), () => t, () => []);
            Check(ReferenceEquals(engine.Latest, engine.Latest), "unchanged telemetry getter reuses composite record");
            var readOnly = new Engine(_ => throw new Exception("No USB allowed"), () => t with { Time = DateTimeOffset.UtcNow }, () => ["TTRGBPlus"]);
            var monitoring = new Settings { AllowMonitoringOnly = true };
            try
            {
                readOnly.Start(monitoring, true);
                Check(SpinWait.SpinUntil(() => readOnly.Latest.Time != DateTimeOffset.MinValue, 3000), "monitor-only samples without a Thermaltake device");
                var output = monitoring.Copy(); output.Panel.Enabled = true; bool blocked = false;
                try { readOnly.ValidateLiveSettings(output); } catch (InvalidOperationException) { blocked = true; }
                Check(blocked, "vendor app can coexist with monitoring-only, but prevents enabling USB control");
                Check(readOnly.Running && !readOnly.Cancelled && !readOnly.ControllerFault, "monitor-only does not invent USB faults or cancel for another controller");
            }
            finally { Task.Run(async () => await readOnly.Stop()).GetAwaiter().GetResult(); }
            var metrics = LogMetricsParser.Parse("prompt eval time = 1000 ms / 1250 tokens (0.8 ms per token, 1250.0 tokens per second)\neval time = 1000 ms / 32 tokens (31.25 ms per token, 32.0 tokens per second)\nn_ctx_slot=57344\nn_past=12345");
            var existing = new LcdWidget { Source = WidgetSource.TopGpuProcessLog, Width = 319, Height = 89, FontSize = 18, LogLines = 3, LogDisplay = LogDisplayMode.Tail, TextTemplate = "{value}", X = 0, Y = 0 };
            var actual = new LogExcerpt("server.log", "verbose payload", "OK", DateTimeOffset.Now)
            { IsAiLog = true, WrittenTime = DateTimeOffset.Now, Metrics = new() { GenerationTokensPerSecond = 37.7, PromptTokensPerSecond = 1683.8, ContextUsed = 57344, ContextCapacity = 73728, GenerationReportedAt = DateTimeOffset.Now.AddMinutes(-5) } };
            string actualText = LogPresentation.Format(existing, actual, DateTimeOffset.Now);
            using (var canvas = new Bitmap(319, 89))
            using (var graphics = Graphics.FromImage(canvas))
            {
                var measured = renderer.MeasureLogText(graphics, existing, actualText);
                Check(measured.Width <= 319 && measured.Height <= 89 && actualText.StartsWith("OLD GEN 37.7 tok/s"), "existing 319x89 font18 legacy profile shows throughput first without clipping");
            }
            var exact = new DisplaySettings { Enabled = true, UseCustomLayout = true, Widgets = [existing] };
            using (var frame = renderer.Render(t with { LogExcerpts = new Dictionary<string, LogExcerpt> { ["process:gpu"] = actual } }, exact, 319, 89))
                frame.Save(Path.Combine(directory, "existing-ai-widget-319x89.png"), ImageFormat.Png);
            foreach (var presetId in new[] { "wide-ai-log", "tall-ai-log", "round-ai-log", "wide-game-log", "tall-game-log", "round-game-log" })
            {
                bool round = presetId.StartsWith("round", StringComparison.Ordinal), game = presetId.Contains("game", StringComparison.Ordinal);
                var preset = LcdPresets.Create(new(), presetId, 480, round ? 480 : 128);
                var excerpt = new LogExcerpt("demo.log", "reported data", "OK", DateTimeOffset.Now)
                { WrittenTime = DateTimeOffset.Now, Metrics = game ? LogMetricsParser.Parse("FPS:144 frametime:6.9 ms RTT:23 ms") : metrics };
                var sample = t with { LogExcerpts = new Dictionary<string, LogExcerpt> { ["process:gpu"] = excerpt } };
                var logWidget = preset.Widgets.Last();
                using (var canvas = new Bitmap(480, 480))
                using (var graphics = Graphics.FromImage(canvas))
                {
                    var measured = renderer.MeasureLogText(graphics, logWidget, WidgetLayout.Format(logWidget, sample));
                    Check(measured.Width <= logWidget.Width && measured.Height <= logWidget.Height,
                        "populated log fits without clipping or ellipsis: " + presetId);
                }
                using var rendered = renderer.Render(sample, preset, 480, round ? 480 : 128);
                if (preset.Rotation is 90 or 270) rendered.RotateFlip(RotateFlipType.Rotate270FlipNone);
                rendered.Save(Path.Combine(directory, presetId + "-populated.png"), ImageFormat.Png);
                Check(WidgetLayout.Format(preset.Widgets.Last(), sample).Contains(game ? "144" : "32.0"), "populated compact preset prioritizes the key metric: " + presetId);
            }
            renderer.Dispose(); renderer.Dispose();
            Check(true, "renderer disposal is idempotent when WinForms closes/disposes twice");
        }
        finally { Directory.Delete(root, recursive: true); }
        return count;
    }
}
