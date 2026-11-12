using System.Text;
using System.Text.RegularExpressions;

namespace ThermalDesk.Core;

public sealed record LogExcerpt(string Path, string Text, string Status, DateTimeOffset Time)
{
    public LogMetrics Metrics { get; init; } = LogMetrics.Empty;
    public DateTimeOffset WrittenTime { get; init; }
    public bool IsAiLog { get; init; }
    public static IReadOnlyDictionary<string, LogExcerpt> Empty { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, LogExcerpt>(new Dictionary<string, LogExcerpt>());
}

public static class LogWidget
{
    public static bool IsLog(WidgetSource source) => source is WidgetSource.TextFileLog or WidgetSource.TopCpuProcessLog or WidgetSource.TopGpuProcessLog;
    public static string Key(LcdWidget widget) => widget.Source switch
    {
        WidgetSource.TopCpuProcessLog => "process:cpu", WidgetSource.TopGpuProcessLog => "process:gpu",
        _ => "file:" + widget.LogFilePath.Trim().ToUpperInvariant()
    };
    public static IEnumerable<LcdWidget> Selected(Settings settings) => new[] { settings.Panel, settings.Aio }
        .Where(d => d.Enabled && d.UseCustomLayout && (d.Content == DisplayContent.Dashboard || d.Overlay))
        .SelectMany(d => d.Widgets).Where(w => IsLog(w.Source));
    public static bool NeedsProcesses(Settings settings) => Selected(settings).Any(w => w.Source != WidgetSource.TextFileLog);
}

public static class BoundedLogTail
{
    public const int MaximumReadBytes = 32768, MaximumTextCharacters = 4096;
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
    private static readonly Regex Secrets = new("(?i)((?:--?)?(?:password|passwd|api[-_]?key|access[-_]?token|refresh[-_]?token|authorization)[\"']?\\s*(?:[:=]\\s*|\\s+))[\"']?[^\\s,;\"'}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
    private static readonly Regex Bearer = new(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));

    public static bool IsLocalPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 500 || path.Any(char.IsControl) ||
                !System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
            if (OperatingSystem.IsWindows())
            {
                if (path.Length < 3 || path[1] != ':' || path.AsSpan(2).Contains(':')) return false;
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "LPT1" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
                if (new DriveInfo(System.IO.Path.GetPathRoot(path)!).DriveType is DriveType.Network or DriveType.NoRootDirectory) return false;
            }
            return true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }
    public static bool HasReparseAncestor(string path)
    {
        for (string? current = System.IO.Path.GetFullPath(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
    public static LogExcerpt Read(string path)
    {
        var time = DateTimeOffset.UtcNow;
        try
        {
            if (!IsLocalPath(path)) return new(path, "", "Select a local text/log file", time);
            if (HasReparseAncestor(path)) return new(path, "", "Linked paths are not read", time);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            long length = stream.Length;
            Span<byte> bom = stackalloc byte[4]; int bomBytes = stream.Read(bom);
            Encoding encoding = bomBytes >= 2 && bom[0] == 0xff && bom[1] == 0xfe ? Encoding.Unicode
                : bomBytes >= 2 && bom[0] == 0xfe && bom[1] == 0xff ? Encoding.BigEndianUnicode : Encoding.UTF8;
            string? csvHeader = null; int budget = MaximumReadBytes - bomBytes;
            if (System.IO.Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                stream.Position = 0; byte[] header = new byte[(int)Math.Min(4096, length)]; int headerRead = stream.Read(header, 0, header.Length);
                csvHeader = encoding.GetString(header, 0, headerRead).Split('\n')[0].TrimEnd('\r'); budget -= headerRead;
            }
            long offset = Math.Max(0, length - budget);
            if (encoding != Encoding.UTF8 && offset % 2 != 0) offset++;
            stream.Position = offset;
            byte[] bytes = new byte[(int)Math.Min(budget, length - offset)];
            int count = 0, read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0) count += read;
            string text = encoding.GetString(bytes, 0, count); long textOffset = offset;
            if (text.StartsWith('\uFEFF')) { textOffset += encoding.GetByteCount(text.AsSpan(0, 1)); text = text[1..]; }
            if (offset > 0)
            {
                int firstLine = text.IndexOf('\n');
                if (firstLine >= 0)
                {
                    // A UTF-8 window may start inside a multibyte character. The
                    // decoder's replacement character must not skew absolute
                    // report offsets and make an old timestamp-free rate fresh.
                    textOffset = encoding == Encoding.UTF8 ? offset + Array.IndexOf(bytes, (byte)'\n', 0, count) + 1
                        : textOffset + encoding.GetByteCount(text.AsSpan(0, firstLine + 1));
                    text = text[(firstLine + 1)..];
                }
                // A single huge line still has a bounded, useful suffix.
            }
            var metrics = LogMetricsParser.Parse(text, csvHeader);
            metrics = metrics with
            {
                GenerationReportOffset = metrics.GenerationReportOffset >= 0 ? textOffset + encoding.GetByteCount(text.AsSpan(0, (int)metrics.GenerationReportOffset)) : -1,
                PromptReportOffset = metrics.PromptReportOffset >= 0 ? textOffset + encoding.GetByteCount(text.AsSpan(0, (int)metrics.PromptReportOffset)) : -1
            };
            text = Clean(text);
            DateTimeOffset written = File.GetLastWriteTimeUtc(path);
            metrics = metrics with
            {
                GenerationReportedAt = metrics.GenerationTokensPerSecond.HasValue && metrics.GenerationReportedAt == default ? written : metrics.GenerationReportedAt,
                PromptReportedAt = metrics.PromptTokensPerSecond.HasValue && metrics.PromptReportedAt == default ? written : metrics.PromptReportedAt
            };
            return new(path, text, text.Length == 0 ? "Log is empty" : "Local log · common credentials redacted", time)
            { Metrics = metrics, WrittenTime = written };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or RegexMatchTimeoutException)
        { return new(path, "", "Log unavailable · " + e.GetType().Name, time); }
    }
    public static string Clean(string text)
    {
        // Public helper also bounds arbitrary callers; no entire-file split or regex.
        if (text.Length > MaximumReadBytes) text = text[^MaximumReadBytes..];
        // Sparse/binary-tainted tails can contain thousands of NUL controls;
        // discard them before running bounded credential regexes. Keep ESC until
        // ANSI stripping, and keep line breaks so words cannot cross records.
        text = new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t' or '\u001b').ToArray());
        // Mask the entire bearer value before the generic Authorization matcher
        // can replace only its first word and leave the actual token behind.
        text = Secrets.Replace(Bearer.Replace(Ansi.Replace(text, ""), "Bearer [REDACTED]"), "$1[REDACTED]");
        var clean = new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
        string result = string.Join('\n', clean.TrimEnd('\r', '\n').Split('\n').TakeLast(10).Select(line =>
        {
            line = line.TrimEnd('\r').Replace('\t', ' ');
            // Preserve the useful END of a long status line, rather than spending
            // the entire tiny LCD on timestamp/request prefixes or JSON payloads.
            return line.Length > 400 ? "…" + line[^399..] : line;
        }));
        return result.Length > MaximumTextCharacters ? result[^MaximumTextCharacters..] : result;
    }
}
