using ThermalDesk.Core;

internal static class LogMetricsTests
{
    internal static void Run(Action<bool, string> check)
    {
        string log = "[DEBUG] slot print_timing: id 0 | prompt eval time = 1000 ms / 1250 tokens (0.80 ms per token, 1250.00 tokens per second)\n" +
            "slot print_timing: id 0 | eval time = 1000 ms / 32 tokens (31.25 ms per token, 32.00 tokens per second)\nn_ctx_slot = 57344\nn_past = 12345";
        var stats = LogMetricsParser.Parse(log);
        check(stats.GenerationTokensPerSecond == 32 && stats.PromptTokensPerSecond == 1250 && stats.GeneratedTokens == 32, "llama timings distinguish generation from prompt throughput");
        check(stats.ContextUsed == 12345 && stats.ContextCapacity == 57344, "context capacity is per-slot, not divided or inferred from total allocation");
        check(LogMetricsParser.Parse("prompt: 12345\nn_ctx=114688").PromptTokensPerSecond is null && LogMetricsParser.Parse("n_ctx=114688").ContextCapacity is null, "token count and whole-backend context are not misreported as speed / per-slot context");
        var json = LogMetricsParser.Parse("{\"predicted_per_second\":31.5,\"prompt_per_second\":1500.2}");
        check(json.GenerationTokensPerSecond == 31.5 && json.PromptTokensPerSecond == 1500.2, "explicit llama API timing field names supported");
        check(LogMetricsParser.Parse("generation: 20 tok/s\ngeneration: 40 tok/s").GenerationTokensPerSecond == 40, "last reported speed wins");
        string strataLog = "strata serve: prompt 10469 tokens = 10451 reused + 18 read in 133 ms (135.0 tok/s), 30 generated in 396 ms (75.7 tok/s), drafts accepted 15 of 17, 3 checkpoints";
        var strata = LogMetricsParser.Parse(strataLog);
        check(strata.GenerationTokensPerSecond == 75.7 && strata.PromptTokensPerSecond == 135 && strata.GeneratedTokens == 30,
            "real Strata inference summary distinguishes decode from new-token prefill throughput");
        check(strata.ContextUsed == 10469 && strata.ContextCapacity is null && strata.PromptReusedTokens == 10451 && strata.PromptNewTokens == 18,
            "Strata prompt totals and reused/new counts are reported without inventing a context capacity");
        check(Math.Abs(strata.PromptCacheHitPercent!.Value - 99.8281) < 0.001,
            "Strata prefix cache percentage uses reused prompt tokens, not generated tokens");
        var strataFresh = LogMetricsParser.Parse(strataLog.Replace("10451 reused + 18", "0 reused + 10469"));
        check(strataFresh.PromptCacheHitPercent == 0, "Strata full-prefill report has zero cache hits");
        check(LogMetricsParser.Parse(strataLog.Replace("10451 reused + 18", "10452 reused + 18")).GenerationTokensPerSecond is null,
            "inconsistent Strata token counts reject the whole timing report");
        check(LogMetricsParser.Parse(strataLog.Replace("10469 tokens", "999999999999 tokens")).GenerationTokensPerSecond is null,
            "overflowing Strata prompt counts cannot leak partial performance values");
        check(LogMetricsParser.Parse(strataLog.Replace("135.0 tok/s", "NaN tok/s")).GenerationTokensPerSecond is null,
            "invalid Strata timing does not fabricate partial valid-looking rates");
        check(LogMetricsParser.Parse(strataLog + "\ngeneration: 99 tok/s").GenerationTokensPerSecond == 99 &&
            LogMetricsParser.Parse(log + "\n" + strataLog).GenerationTokensPerSecond == 75.7,
            "newest inference report wins across Strata and other supported formats");
        check(LogMetricsParser.Parse("unrelated request noise").RetainAi(strata).PromptCacheHitPercent == strata.PromptCacheHitPercent &&
            LogMetricsParser.Parse("generation: 22 tok/s").RetainAi(strata).PromptCacheHitPercent is null,
            "cache metrics survive append noise but are not inherited by a different timing format");
        var reported = DateTimeOffset.Parse("2026-11-12T19:44:53+01:00");
        var timed = LogMetricsParser.Parse("[2026-11-12T19:44:53+01:00][DEBUG] I slot print_timing: id 1 | task 14 | eval time = 23571.57 ms / 924 tokens (25.51 ms per token, 39.16 tokens per second)");
        check(timed.GenerationTokensPerSecond == 39.16 && timed.GeneratedTokens == 924 && timed.GenerationReportedAt == reported, "real LM Studio slot timing includes the original report time, not the newest file write");
        var retained = LogMetricsParser.Parse("[DEBUG] verbose new request body without timing").RetainAi(timed);
        check(retained.GenerationTokensPerSecond == 39.16 && retained.GenerationReportedAt == reported, "new request noise retains completed inference without refreshing its age");
        check(LogMetricsParser.Parse(log + "\n{\"predicted_per_second\":44.2}").GenerationTokensPerSecond == 44.2,
            "the newest timing wins across mixed llama timing and JSON formats");
        check(LogMetricsParser.Parse("generation: NaN tok/s\nprompt: -123 tok/s\nGPU latency: 5 ms").GenerationTokensPerSecond is null && LogMetricsParser.Parse("GPU latency: 5 ms").NetworkLatencyMilliseconds is null, "invalid speed and GPU latency cannot become game network RTT");
        var gaming = LogMetricsParser.Parse("FPS: 144 frametime=6.94 ms ping: 23 ms");
        check(gaming.Fps == 144 && gaming.FrameMilliseconds == 6.94 && gaming.NetworkLatencyMilliseconds == 23, "game-reported FPS / frame time / ping parsed separately");
        check(LogMetricsParser.Parse("60 fps RTT=45 ms").Fps == 60 && LogMetricsParser.Parse("60 fps RTT=45 ms").NetworkLatencyMilliseconds == 45, "alternate reported FPS and RTT supported");
        var csv = LogMetricsParser.Parse("game.exe,12,10,4\ngame.exe,12,8,3", "Application,ProcessID,MsBetweenPresents,MsPCLatency");
        check(csv.Fps == 125 && csv.PresentationCadence && csv.PcLatencyMilliseconds == 3 && csv.NetworkLatencyMilliseconds is null, "PresentMon presentation cadence / PC latency are not network ping");
        check(LogMetricsParser.Parse("game.exe,12,10\nother.exe,13,8", "Application,ProcessID,MsBetweenPresents").Fps is null, "mixed-process CSV rejected rather than showing another game's FPS");
        check(LogMetricsParser.Parse("game.exe,12,0\ngame.exe,12,NA", "Application,ProcessID,MsBetweenPresents").Fps is null, "invalid CSV frame intervals cannot fabricate FPS");
        var now = DateTimeOffset.UtcNow;
        var entry = new LogExcerpt("sample.log", log, "OK", now) { Metrics = stats, WrittenTime = now };
        var widget = new LcdWidget { Source = WidgetSource.TextFileLog, TextTemplate = "{value}", Width = 440, Height = 100, FontSize = 20, LogDisplay = LogDisplayMode.Auto };
        string text = LogPresentation.Format(widget, entry, now);
        check(LogPresentation.Format(widget, entry with { Text = strataLog, Metrics = strata }, now).Contains("CACHE 99.8%"),
            "wide compact AI widget exposes actual Strata cache efficiency");
        check(text.StartsWith("LAST GEN 32.0 tok/s") && text.Contains("LAST PROMPT 1250 tok/s"), "auto compact AI mode prioritizes correctly labeled rates");
        widget.Source = WidgetSource.TopGpuProcessLog; widget.LogDisplay = LogDisplayMode.Tail;
        check(LogPresentation.Format(widget, entry, now).StartsWith("LAST GEN 32.0 tok/s"), "legacy top GPU Tail widget automatically exposes actual AI throughput");
        check(LogPresentation.Format(widget, entry with { Metrics = LogMetrics.Empty, IsAiLog = true }, now).Contains("LAST GEN — tok/s"),
            "known AI backend without completed timing shows unavailable rates instead of payload noise");
        widget.LogDisplay = LogDisplayMode.RawTail;
        check(!LogPresentation.Format(widget, entry, now).StartsWith("LAST GEN"), "explicit RawTail preserves raw text even for AI processes");
        widget.LogDisplay = LogDisplayMode.Auto;
        var aged = entry with { Metrics = retained, WrittenTime = reported.AddHours(1) };
        check(LogPresentation.Format(widget, aged, reported.AddMinutes(5)).StartsWith("OLD GEN 39.2 tok/s") && LogPresentation.Format(widget, aged, reported.AddMinutes(5)).Contains("5m ago"), "AI freshness follows inference report time, not unrelated log writes");
        widget.LogLines = 1;
        check(LogPresentation.Format(widget, entry, now).StartsWith("LAST GEN"), "single-line AI widget prioritizes generation, not context footer");
        widget.Width = 104; widget.Height = 230; widget.LogLines = 6;
        check(LogPresentation.Format(widget, entry, now).Split('\n').Length == 6, "portrait uses paired short label/value lines");
        widget.Width = 440; widget.Height = 100; widget.LogLines = 3; widget.LogDisplay = LogDisplayMode.Gaming;
        var gameEntry = entry with { Metrics = csv, WrittenTime = now.AddMinutes(-2) };
        string gameText = LogPresentation.Format(widget, gameEntry, now);
        check(gameText.StartsWith("OLD PRES FPS 125") && gameText.Contains("PC LAT 3.0 ms") && !gameText.Contains("NET 3"), "stale rendering data labeled, never called online latency");
        widget.LogDisplay = LogDisplayMode.Compact;
        check(LogPresentation.Format(widget, entry with { Text = "[2026-11-12][DEBUG] Useful status" }, now) == "Useful status", "compact log removes common prefixes");
        check(LogPresentation.Format(widget, entry with { Text = "", Status = "No local log found" }, now) == "No local log found", "missing source remains explicit in compact modes");
        string root = Directory.CreateTempSubdirectory("thermaldesk-csv-tests-").FullName;
        try
        {
            string path = Path.Combine(root, "game.csv");
            File.WriteAllText(path, "Application,ProcessID,MsBetweenPresents\ngame.exe,12,10\n");
            var parsed = BoundedLogTail.Read(path);
            check(parsed.Metrics.Fps == 100 && parsed.Metrics.PresentationCadence, "bounded file reader combines CSV header and tail");
            string longLine = new string('p', 1000) + " useful final status";
            check(BoundedLogTail.Clean(longLine).EndsWith("useful final status") && BoundedLogTail.Clean(longLine).Length <= 400, "long raw log lines preserve the useful suffix within the fixed cap");
        }
        finally { Directory.Delete(root, true); }
        var copy = new DisplaySettings { Widgets = [widget] }.Copy().Widgets[0];
        check(copy.LogDisplay == widget.LogDisplay && copy.LogAutoFit && copy.LogStaleSeconds == 60, "compact options persist; legacy profiles retain Tail default");
    }
}
