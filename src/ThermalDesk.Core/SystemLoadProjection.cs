namespace ThermalDesk.Core;

public readonly record struct SystemTimeSample(ulong Idle, ulong Kernel, ulong User);
public static class SystemLoadProjection
{
    public static double? CpuLoad(SystemTimeSample before, SystemTimeSample after)
    {
        if (after.Idle < before.Idle || after.Kernel < before.Kernel || after.User < before.User) return null;
        double total = (double)(after.Kernel - before.Kernel) + (after.User - before.User);
        double idle = after.Idle - before.Idle;
        // Kernel time already includes idle time. Invalid/reset counters stay absent.
        return total > 0 && idle <= total ? (total - idle) / total * 100 : null;
    }
    public static double? PhysicalMemoryLoad(ulong total, ulong available) => total > 0 && available <= total ? (total - available) / (double)total * 100 : null;
}
