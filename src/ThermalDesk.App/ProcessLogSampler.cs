using System.Diagnostics;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// Used only by the optional log worker, never by a thermal/control/paint callback.
internal sealed class ProcessLogSampler
{
    private sealed record CachedTail(long Length, DateTime Written, DateTime Created, LogExcerpt Value);
    private sealed record Discovery(string Identity, DateTimeOffset Expires, string Path);
    private readonly Dictionary<string, CachedTail> tails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Discovery> discoveries = new();
    private readonly Func<ProcessUse, string> discover;
    internal int FileReads { get; private set; }
    internal int DiscoveryAttempts { get; private set; }
    public ProcessLogSampler(Func<ProcessUse, string>? discoverLog = null) { discover = discoverLog ?? Discover; }
    public IReadOnlyDictionary<string, LogExcerpt> Read(Settings settings, Telemetry telemetry)
    {
        var selected = LogWidget.Selected(settings).GroupBy(LogWidget.Key).Select(g => g.First()).ToArray();
        if (selected.Length == 0) { tails.Clear(); discoveries.Clear(); return LogExcerpt.Empty; }
        var result = new Dictionary<string, LogExcerpt>();
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var widget in selected)
        {
            string key = LogWidget.Key(widget), path = widget.LogFilePath;
            bool ai = false;
            if (widget.Source != WidgetSource.TextFileLog)
            {
                var process = widget.Source == WidgetSource.TopCpuProcessLog ? telemetry.TopCpuProcess : telemetry.TopGpuProcess;
                if (process is null) { result[key] = new("", "", "Waiting for process sample", DateTimeOffset.UtcNow); continue; }
                ai = IsAiProcess(process.Name);
                string identity = $"{process.Pid}:{process.StartedTicks}:{process.Name}";
                if (!discoveries.TryGetValue(key, out var old) || old.Identity != identity || old.Expires <= DateTimeOffset.UtcNow)
                {
                    if (old is not null && old.Identity != identity) tails.Remove(old.Path);
                    DiscoveryAttempts++;
                    discoveries[key] = old = new(identity, DateTimeOffset.UtcNow.AddSeconds(60), discover(process));
                }
                path = old.Path;
                if (path.Length == 0) { result[key] = new("", "", $"No local log found · {process.Name}", DateTimeOffset.UtcNow); continue; }
            }
            retained.Add(path);
            result[key] = ReadCached(path) with { IsAiLog = ai };
        }
        foreach (var stale in tails.Keys.Where(k => !retained.Contains(k)).ToArray()) tails.Remove(stale);
        foreach (var stale in discoveries.Keys.Where(k => !result.ContainsKey(k)).ToArray()) discoveries.Remove(stale);
        return result;
    }
    private static bool IsStrataProcess(string name) => name.Equals("strata", StringComparison.OrdinalIgnoreCase);
    private static bool IsAiProcess(string name) => IsStrataProcess(name) || name.Equals("llama-server", StringComparison.OrdinalIgnoreCase) || name.Equals("Bionic", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LM Studio", StringComparison.OrdinalIgnoreCase);
    private LogExcerpt ReadCached(string path)
    {
        try
        {
            if (!BoundedLogTail.IsLocalPath(path)) return new(path, "", "Select a local text/log file", DateTimeOffset.UtcNow);
            if (BoundedLogTail.HasReparseAncestor(path)) return new(path, "", "Linked paths are not read", DateTimeOffset.UtcNow);
            var file = new FileInfo(path);
            if (tails.TryGetValue(path, out var cached) && file.Length == cached.Length && file.LastWriteTimeUtc == cached.Written && file.CreationTimeUtc == cached.Created &&
                DateTimeOffset.UtcNow - cached.Value.Time < TimeSpan.FromMinutes(1)) return cached.Value;
            FileReads++;
            var value = BoundedLogTail.Read(path);
            if (tails.TryGetValue(path, out var previous) && file.Length >= previous.Length &&
                file.LastWriteTimeUtc >= previous.Written && file.CreationTimeUtc == previous.Created && value.Text.Length > 0)
                value = value with { Metrics = value.Metrics.RetainAi(previous.Value.Metrics) };
            tails[path] = new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc, value);
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { tails.Remove(path); return new(path, "", "Log unavailable · " + e.GetType().Name, DateTimeOffset.UtcNow); }
    }
    private static string Discover(ProcessUse sample)
    {
        try
        {
            using var process = Process.GetProcessById(sample.Pid);
            if (!process.ProcessName.Equals(sample.Name, StringComparison.OrdinalIgnoreCase) ||
                sample.StartedTicks == 0 || process.StartTime.ToUniversalTime().Ticks != sample.StartedTicks) return "";
            string executable = process.MainModule?.FileName ?? "";
            string folder = Path.GetDirectoryName(executable) ?? "";
            return FindPreferredCandidate(sample.Name, CandidateDirectories(sample.Name, folder));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return ""; }
    }
    internal static string FindPreferredCandidate(string name, IEnumerable<string> directories)
    {
        var candidates = directories.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
        // The backend timing log is authoritative. A newer updater/frontend log
        // next to the executable must not displace it simply because it is newer.
        if (IsStrataProcess(name))
        {
            string engine = FindCandidate(candidates.Take(1), path => Path.GetFileName(path).StartsWith("strata-", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase));
            if (engine.Length > 0) return engine;
        }
        int known = IsStrataProcess(name) ? 2 : IsAiProcess(name) ? 3
            : name.Contains("opencode", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        string preferred = FindCandidate(candidates.Take(known));
        return preferred.Length > 0 ? preferred : FindCandidate(candidates.Skip(known));
    }
    internal static IEnumerable<string> CandidateDirectories(string name, string executableFolder)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (IsStrataProcess(name))
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "Strata", "install");
            yield return Path.Combine(local, "Strata", "logs");
        }
        if (name.Equals("llama-server", StringComparison.OrdinalIgnoreCase) || name.Equals("Bionic", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LM Studio", StringComparison.OrdinalIgnoreCase))
        {
            string logs = Path.Combine(home, ".lmstudio", "apps", "bionic", "server-logs");
            yield return Path.Combine(logs, DateTime.Now.ToString("yyyy-MM"));
            yield return Path.Combine(logs, DateTime.Now.AddMonths(-1).ToString("yyyy-MM"));
            yield return Path.Combine(home, ".lmstudio", "server-logs", DateTime.Now.ToString("yyyy-MM"));
        }
        if (name.Contains("opencode", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(home, ".local", "share", "opencode", "log");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "opencode", "log");
        }
        if (executableFolder.Length > 0)
        {
            yield return Path.Combine(executableFolder, "logs");
            yield return Path.Combine(executableFolder, "log");
            yield return executableFolder;
        }
    }
    internal static string FindCandidate(IEnumerable<string> directories, Func<string, bool>? accept = null)
    {
        string best = ""; DateTime latest = DateTime.MinValue; int entries = 0;
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase).Take(6))
        {
            try
            {
                if (!BoundedLogTail.IsLocalPath(Path.Combine(directory, "probe.log")) || !Directory.Exists(directory) || BoundedLogTail.HasReparseAncestor(directory)) continue;
                foreach (string path in Directory.EnumerateFiles(directory).Take(128))
                {
                    if (++entries > 384) return best;
                    if (accept is not null && !accept(path)) continue;
                    string extension = Path.GetExtension(path);
                    if (!new[] { ".log", ".txt", ".out", ".err", ".jsonl" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
                    if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).Contains("log", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(path);
                    if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0) continue;
                    if (info.LastWriteTimeUtc > latest) { latest = info.LastWriteTimeUtc; best = path; }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return best;
    }
}
