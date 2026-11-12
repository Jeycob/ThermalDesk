using System.Runtime.InteropServices;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// Vendor-independent low-cost OS fallback; never invents a CPU/GPU temperature.
internal sealed class WindowsSystemMonitor
{
    private SystemTimeSample? previous;
    internal (double? CpuLoad, double? MemoryLoad) Read()
    {
        double? cpu = null, memory = null;
        try
        {
            // GetSystemTimes covers one group on >64-processor Windows systems.
            // Do not present such a partial aggregate as whole-PC load.
            if (GetActiveProcessorGroupCount() == 1 && GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
            {
                var current = new SystemTimeSample(idle, kernel, user);
                if (previous is { } before) cpu = SystemLoadProjection.CpuLoad(before, current);
                previous = current;
            }
            var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref state)) memory = SystemLoadProjection.PhysicalMemoryLoad(state.TotalPhysical, state.AvailablePhysical);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return (cpu, memory);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
