using System.Globalization;
namespace ThermalDesk.Core;

public sealed record ProcessUse(int Pid, string Name, double Percent, string Engine = "")
{ public long StartedTicks { get; init; } public string Summary => $"{Name} [{Pid}] {Percent.ToString("0.0", CultureInfo.InvariantCulture)}%"; }
public sealed record ProcessCpuSample(int Pid, long StartedTicks, string Name, double CpuSeconds);
public sealed record GpuEngineSample(int Pid, string Engine, double Percent);
public sealed record ProcessActivity(DateTimeOffset Time, ProcessUse? Cpu, ProcessUse? Gpu, string Status)
{ public static ProcessActivity Disabled => new(DateTimeOffset.MinValue, null, null, "Process monitoring disabled"); }
public static class ProcessActivityProjection
{
    public static ProcessUse? TopCpu(IReadOnlyList<ProcessCpuSample> before, IReadOnlyList<ProcessCpuSample> after, double seconds, int logicalProcessors)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || logicalProcessors <= 0) return null;
        var previous = before.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        return after.Where(p => p.Pid > 0 && previous.TryGetValue(p.Pid, out var old) && old.StartedTicks == p.StartedTicks)
            .Select(p => new ProcessUse(p.Pid, p.Name, (p.CpuSeconds - previous[p.Pid].CpuSeconds) / seconds / logicalProcessors * 100) { StartedTicks = p.StartedTicks })
            .Where(p => double.IsFinite(p.Percent) && p.Percent is > 0 and <= 100.1)
            .OrderByDescending(p => p.Percent).ThenBy(p => p.Pid).FirstOrDefault();
    }
    public static ProcessUse? TopGpu(IReadOnlyList<GpuEngineSample> engines, IReadOnlyList<ProcessCpuSample> processes)
    {
        var names = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Name);
        // Task Manager uses the busiest engine across all adapters for a process,
        // not a sum of overlapping 3D/compute/copy engines that can exceed 100%.
        return engines.Where(e => e.Pid > 0 && names.ContainsKey(e.Pid) && double.IsFinite(e.Percent) && e.Percent is > 0 and <= 100.1)
            .GroupBy(e => e.Pid).Select(g => g.OrderByDescending(e => e.Percent).First())
            .Select(e => new ProcessUse(e.Pid, names[e.Pid], Math.Min(e.Percent, 100), e.Engine) { StartedTicks = processes.First(p => p.Pid == e.Pid).StartedTicks })
            .OrderByDescending(p => p.Percent).ThenBy(p => p.Pid).FirstOrDefault();
    }
}
