using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ThermalDesk.Core;

public enum LogDisplayMode { Tail, Compact, AiStats, Gaming, Auto, RawTail }
public sealed record LogMetrics
{
    public static LogMetrics Empty { get; } = new();
    public double? GenerationTokensPerSecond { get; init; }
    public double? PromptTokensPerSecond { get; init; }
    public int? GeneratedTokens { get; init; }
    public int? ContextUsed { get; init; }
    public int? ContextCapacity { get; init; }
    public int? PromptTotalTokens { get; init; }
    public int? PromptReusedTokens { get; init; }
    public int? PromptNewTokens { get; init; }
    public double? PromptCacheHitPercent => PromptTotalTokens is > 0 && PromptReusedTokens is >= 0 &&
        PromptReusedTokens <= PromptTotalTokens ? 100.0 * PromptReusedTokens / PromptTotalTokens : null;
    public double? Fps { get; init; }
    public double? FrameMilliseconds { get; init; }
    public double? NetworkLatencyMilliseconds { get; init; }
    public double? PcLatencyMilliseconds { get; init; }
    public bool PresentationCadence { get; init; }
    public DateTimeOffset GenerationReportedAt { get; init; }
    public DateTimeOffset PromptReportedAt { get; init; }
    public long GenerationReportOffset { get; init; } = -1;
    public long PromptReportOffset { get; init; } = -1;
    public bool HasAiRates => GenerationTokensPerSecond.HasValue || PromptTokensPerSecond.HasValue;
    // Keep the last completed inference visible through subsequent request/payload
    // noise. Only the caller's same-file, append-only cache may use this merge.
    public LogMetrics RetainAi(LogMetrics previous) => this with
    {
        GenerationTokensPerSecond = GenerationTokensPerSecond ?? previous.GenerationTokensPerSecond,
        PromptTokensPerSecond = PromptTokensPerSecond ?? previous.PromptTokensPerSecond,
        GeneratedTokens = GeneratedTokens ?? previous.GeneratedTokens,
        ContextUsed = ContextUsed ?? previous.ContextUsed, ContextCapacity = ContextCapacity ?? previous.ContextCapacity,
        PromptTotalTokens = PromptTotalTokens ?? (HasAiRates ? null : previous.PromptTotalTokens),
        PromptReusedTokens = PromptReusedTokens ?? (HasAiRates ? null : previous.PromptReusedTokens),
        PromptNewTokens = PromptNewTokens ?? (HasAiRates ? null : previous.PromptNewTokens),
        GenerationReportedAt = !GenerationTokensPerSecond.HasValue || GenerationReportOffset == previous.GenerationReportOffset && GenerationTokensPerSecond == previous.GenerationTokensPerSecond ? previous.GenerationReportedAt : GenerationReportedAt,
        PromptReportedAt = !PromptTokensPerSecond.HasValue || PromptReportOffset == previous.PromptReportOffset && PromptTokensPerSecond == previous.PromptTokensPerSecond ? previous.PromptReportedAt : PromptReportedAt,
        GenerationReportOffset = GenerationTokensPerSecond.HasValue ? GenerationReportOffset : previous.GenerationReportOffset,
        PromptReportOffset = PromptTokensPerSecond.HasValue ? PromptReportOffset : previous.PromptReportOffset
    };
}

public static class LogMetricsParser
{
    // Linear-time matching avoids backtracking and the cold compiled-matcher JIT
    // falling inside the first 30ms deadline. Keep the bounded input and timeout.
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
    private static readonly Regex Timing = Pattern(@"(?m)^[^\r\n]*?(?<kind>prompt\s+eval|eval)\s+time\s*=[^\r\n]*?/\s*(?<tokens>\d+)\s+tokens[^\r\n]*?[, ](?<v>\d+(?:\.\d+)?)\s+tokens\s+per\s+second");
    private static readonly Regex Generation = Pattern(@"(?:\b(?:generation|decode)(?:\s+(?:speed|rate))?\s*[:=]\s*(?<v>\d+(?:\.\d+)?)\s*(?:tokens/s|tok/s|t/s)|\bpredicted_per_second[""']?\s*:\s*(?<v>\d+(?:\.\d+)?))");
    private static readonly Regex Prompt = Pattern(@"(?:\b(?:prompt|prefill)(?:\s+(?:speed|rate))?\s*[:=]\s*(?<v>\d+(?:\.\d+)?)\s*(?:tokens/s|tok/s|t/s)|\bprompt_per_second[""']?\s*:\s*(?<v>\d+(?:\.\d+)?))");
    private static readonly Regex Strata = Pattern(@"(?m)^[^\r\n]*?\bstrata serve:\s+prompt\s+(?<total>\d+)\s+tokens\s*=\s*(?<reused>\d+)\s+reused\s*\+\s*(?<read>\d+)\s+read\s+in\s+\d+(?:\.\d+)?\s+ms\s+\((?<prompt>\d+(?:\.\d+)?)\s+tok/s\),\s*(?<tokens>\d+)\s+generated\s+in\s+\d+(?:\.\d+)?\s+ms\s+\((?<generation>\d+(?:\.\d+)?)\s+tok/s\)");
    private static readonly Regex ContextUsed = Pattern(@"\b(?:n_past|context_used|context_tokens)\s*[:=]\s*(?<v>\d+)");
    private static readonly Regex ContextCapacity = Pattern(@"\b(?:n_ctx_slot|context_capacity)\s*[:=]\s*(?<v>\d+)");
    private static readonly Regex Fps = Pattern(@"(?:\bfps\s*[:=]\s*(?<v>\d+(?:\.\d+)?)|\b(?<v>\d+(?:\.\d+)?)\s+fps\b)");
    private static readonly Regex Frame = Pattern(@"\b(?:frametime|frame_time|frame time)\s*[:=]\s*(?<v>\d+(?:\.\d+)?)\s*ms\b");
    private static readonly Regex Network = Pattern(@"\b(?:ping|rtt|network latency)\s*[:=]\s*(?<v>\d+(?:\.\d+)?)\s*ms\b");
    private static double? Last(Regex pattern, string text, double maximum)
    {
        double? value = null;
        foreach (Match match in pattern.Matches(text))
            if (double.TryParse(match.Groups["v"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double parsed) && parsed >= 0 && parsed <= maximum) value = parsed;
        return value;
    }
    private static DateTimeOffset ReportTime(string text, int index)
    {
        int from = index > 0 ? text.LastIndexOf('\n', index - 1) + 1 : 0;
        int bracket = text.IndexOf(']', from);
        if (bracket < 0 || bracket - from > 50 || text[from] != '[') return default;
        return DateTimeOffset.TryParse(text.AsSpan(from + 1, bracket - from - 1), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var time) ? time : default;
    }
    public static LogMetrics Parse(string text, string? csvHeader = null)
    {
        if (text.Length > BoundedLogTail.MaximumReadBytes) text = text[^BoundedLogTail.MaximumReadBytes..];
        try
        {
            if (csvHeader is not null) return ParseCsv(csvHeader, text);
            double? generation = null, prompt = null; int? tokens = null; int generationIndex = -1, promptIndex = -1;
            int? promptTotal = null, promptReused = null, promptNew = null;
            DateTimeOffset generationTime = default, promptTime = default;
            foreach (Match match in Timing.Matches(text))
            {
                if (!double.TryParse(match.Groups["v"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double rate) || rate is < 0 or > 1000000) continue;
                if (match.Groups["kind"].Value.StartsWith("prompt", StringComparison.OrdinalIgnoreCase)) { prompt = rate; promptTime = ReportTime(text, match.Index); promptIndex = match.Index; }
                else { generation = rate; generationTime = ReportTime(text, match.Index); generationIndex = match.Index; if (int.TryParse(match.Groups["tokens"].Value, out int count)) tokens = count; }
            }
            foreach (Match match in Strata.Matches(text))
            {
                bool Count(string name, out int value) => int.TryParse(match.Groups[name].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
                if (!Count("total", out int total) || !Count("reused", out int reused) || !Count("read", out int fresh) ||
                    !Count("tokens", out int generated) || (long)reused + fresh != total ||
                    !double.TryParse(match.Groups["generation"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double genRate) || genRate is < 0 or > 1000000 ||
                    !double.TryParse(match.Groups["prompt"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double promptRate) || promptRate is < 0 or > 1000000) continue;
                if (match.Index > generationIndex)
                { generation = genRate; tokens = generated; generationTime = ReportTime(text, match.Index); generationIndex = match.Index; }
                if (match.Index > promptIndex)
                {
                    prompt = promptRate; promptTime = ReportTime(text, match.Index); promptIndex = match.Index;
                    promptTotal = total; promptReused = reused; promptNew = fresh;
                }
            }
            foreach (Match match in Generation.Matches(text))
                if (match.Index > generationIndex && double.TryParse(match.Groups["v"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double rate) && rate is >= 0 and <= 1000000)
                { generation = rate; generationTime = ReportTime(text, match.Index); generationIndex = match.Index; tokens = null; }
            foreach (Match match in Prompt.Matches(text))
                if (match.Index > promptIndex && double.TryParse(match.Groups["v"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double rate) && rate is >= 0 and <= 1000000)
                { prompt = rate; promptTime = ReportTime(text, match.Index); promptIndex = match.Index; promptTotal = promptReused = promptNew = null; }
            return new()
            {
                GenerationTokensPerSecond = generation, PromptTokensPerSecond = prompt, GeneratedTokens = tokens,
                GenerationReportedAt = generationTime, PromptReportedAt = promptTime,
                PromptTotalTokens = promptTotal, PromptReusedTokens = promptReused, PromptNewTokens = promptNew,
                GenerationReportOffset = generationIndex, PromptReportOffset = promptIndex,
                ContextUsed = (int?)Last(ContextUsed, text, int.MaxValue) ?? promptTotal, ContextCapacity = (int?)Last(ContextCapacity, text, int.MaxValue),
                Fps = Last(Fps, text, 10000), FrameMilliseconds = Last(Frame, text, 100000), NetworkLatencyMilliseconds = Last(Network, text, 60000)
            };
        }
        catch (RegexMatchTimeoutException) { return LogMetrics.Empty; }
    }
    private static string[] Csv(string line)
    {
        var cells = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
        foreach (char c in line) { if (c == '"') quoted = !quoted; else if (c == ',' && !quoted) { cells.Add(cell.ToString().Trim()); cell.Clear(); } else cell.Append(c); }
        cells.Add(cell.ToString().Trim()); return cells.ToArray();
    }
    private static LogMetrics ParseCsv(string header, string text)
    {
        if (header.Length > 4096) return LogMetrics.Empty;
        var names = Csv(header.TrimStart('\uFEFF')); var records = text.Split('\n').Where(l => l.Length is > 0 and <= 4096).TakeLast(120).Select(Csv).Where(c => c.Length == names.Length).ToArray();
        int Index(string name) => Array.FindIndex(names, c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        int pid = Index("ProcessID"), present = Index("MsBetweenPresents"), fps = Index("FPS"), pcLatency = Index("MsPCLatency");
        // Mixed-process CSVs are ambiguous. Require a per-game capture instead of
        // displaying another application's cadence as the foreground game's FPS.
        if (pid >= 0 && records.Where(r => int.TryParse(r[pid], out _)).Select(r => r[pid]).Distinct().Take(2).Count() > 1) return LogMetrics.Empty;
        foreach (var row in records.Reverse())
        {
            double? Value(int index, double max) => index >= 0 && double.TryParse(row[index], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v >= 0 && v <= max ? v : null;
            double? interval = Value(present, 100000), direct = Value(fps, 10000);
            if (direct is null && interval is not > 0) continue;
            double? rate = direct ?? 1000 / interval;
            if (rate is > 10000) continue;
            return new() { Fps = rate, FrameMilliseconds = interval, PcLatencyMilliseconds = Value(pcLatency, 60000), PresentationCadence = direct is null };
        }
        return LogMetrics.Empty;
    }
}

public static class LogPresentation
{
    private static readonly Regex Prefix = new(@"^(?:\s*\[[^\]\r\n]{1,100}\])+\s*", RegexOptions.Compiled, TimeSpan.FromMilliseconds(30));
    private static string Number(double? value, string format = "0.0") => value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "—";
    public static string Format(LcdWidget widget, LogExcerpt excerpt, DateTimeOffset now)
    {
        if (excerpt.Text.Length == 0) return widget.Width >= 180 ? excerpt.Status
            : excerpt.Status.StartsWith("No local log", StringComparison.Ordinal) ? "No log found" : excerpt.Status.StartsWith("Waiting", StringComparison.Ordinal) ? "Waiting for log" : "Log unavailable";
        var metrics = excerpt.Metrics;
        var mode = widget.LogDisplay;
        // Existing profiles used Tail before compact AI views existed. Upgrade
        // only the automatic GPU source when genuine inference metrics are found;
        // RawTail remains an explicit opt-out, and manual text-file logs are untouched.
        if (mode == LogDisplayMode.Tail && widget.Source == WidgetSource.TopGpuProcessLog && (metrics.HasAiRates || excerpt.IsAiLog)) mode = LogDisplayMode.AiStats;
        if (mode == LogDisplayMode.Auto) mode = metrics.HasAiRates || excerpt.IsAiLog ? LogDisplayMode.AiStats
            : metrics.Fps.HasValue || metrics.NetworkLatencyMilliseconds.HasValue ? LogDisplayMode.Gaming : LogDisplayMode.Compact;
        string text;
        bool narrow = widget.Width < 180;
        if (mode == LogDisplayMode.AiStats)
        {
            var reported = metrics.GenerationReportedAt != default ? metrics.GenerationReportedAt : metrics.PromptReportedAt;
            if (reported == default) reported = excerpt.WrittenTime;
            double age = reported == default ? 0 : Math.Max(0, (now - reported).TotalSeconds);
            string label = age > widget.LogStaleSeconds ? "OLD GEN" : "LAST GEN";
            string ago = reported == default ? "" : age < 60 ? $"{(int)age}s ago" : age < 3600 ? $"{(int)(age / 60)}m ago" : $"{(int)(age / 3600)}h ago";
            string cache = metrics.PromptCacheHitPercent.HasValue ? $" · CACHE {Number(metrics.PromptCacheHitPercent)}%" : "";
            text = narrow ? $"{label}\n{Number(metrics.GenerationTokensPerSecond)} tok/s\nLAST PROMPT\n{Number(metrics.PromptTokensPerSecond, "0")} tok/s\nCTX (LOG)\n{Number(metrics.ContextUsed, "0")}/{Number(metrics.ContextCapacity, "0")}" :
                $"{label} {Number(metrics.GenerationTokensPerSecond)} tok/s\nLAST PROMPT {Number(metrics.PromptTokensPerSecond, "0")} tok/s\nCTX {Number(metrics.ContextUsed, "0")}/{Number(metrics.ContextCapacity, "0")}{cache}{(ago.Length == 0 ? "" : " · " + ago)}";
        }
        else if (mode == LogDisplayMode.Gaming)
        {
            bool old = excerpt.WrittenTime != default && now - excerpt.WrittenTime > TimeSpan.FromSeconds(widget.LogStaleSeconds);
            text = $"{(old ? "OLD " : "")}{(metrics.PresentationCadence ? "PRES FPS" : "LAST FPS")} {Number(metrics.Fps, "0")}\nFRAME {Number(metrics.FrameMilliseconds)} ms\nNET {Number(metrics.NetworkLatencyMilliseconds, "0")} ms";
            if (!metrics.NetworkLatencyMilliseconds.HasValue && metrics.PcLatencyMilliseconds.HasValue) text = text[..text.LastIndexOf('\n')] + $"\nPC LAT {Number(metrics.PcLatencyMilliseconds)} ms";
            if (narrow) text = text.Replace("FPS ", "FPS\n").Replace("FRAME ", "FRAME\n").Replace("NET ", "NET\n").Replace("PC LAT ", "PC LAT\n");
        }
        else
        {
            text = excerpt.Text.Length == 0 ? excerpt.Status : excerpt.Text;
            if (mode == LogDisplayMode.Compact)
                try { text = string.Join('\n', text.Split('\n').Select(l => Prefix.Replace(l, "").Trim())); } catch (RegexMatchTimeoutException) { }
        }
        int count = widget.LogAutoFit ? Math.Min(widget.LogLines, Math.Max(1, (int)(widget.Height / (Math.Min(8, widget.FontSize) * 1.3)))) : widget.LogLines;
        return string.Join('\n', mode is LogDisplayMode.AiStats or LogDisplayMode.Gaming ? text.Split('\n').Take(count) : text.Split('\n').TakeLast(count));
    }
}
