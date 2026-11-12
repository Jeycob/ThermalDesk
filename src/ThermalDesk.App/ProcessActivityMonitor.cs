using System.Diagnostics;
using System.Runtime.InteropServices;
using ThermalDesk.Core;
namespace ThermalDesk.App;

internal sealed class ProcessActivityMonitor : IDisposable
{
    private IReadOnlyList<ProcessCpuSample> previous = [];
    private long previousTick;
    private IntPtr query, gpuCounter;
    private bool gpuAttempted;
    private string gpuStatus = "GPU warming up";
    public ProcessActivity Read()
    {
        long tick = Stopwatch.GetTimestamp();
        var processes = new List<ProcessCpuSample>(); int inaccessible = 0;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
                try { if (p.Id > 0) processes.Add(new(p.Id, p.StartTime.ToUniversalTime().Ticks, p.ProcessName, p.TotalProcessorTime.TotalSeconds)); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { inaccessible++; }
        }
        var cpu = ProcessActivityProjection.TopCpu(previous, processes, previousTick == 0 ? 0 : (tick - previousTick) / (double)Stopwatch.Frequency, Environment.ProcessorCount);
        bool warming = previousTick == 0; previous = processes; previousTick = tick;
        IReadOnlyList<GpuEngineSample> gpu = [];
        try { gpu = ReadGpu(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        { gpuStatus = "GPU process counters unavailable"; CloseQuery(); }
        return new(DateTimeOffset.UtcNow, cpu, ProcessActivityProjection.TopGpu(gpu, processes),
            (warming ? "CPU warming up" : "CPU: total-PC time share") + " · " + gpuStatus + (inaccessible > 0 ? $" · {inaccessible} protected/exited processes skipped" : ""));
    }
    private IReadOnlyList<GpuEngineSample> ReadGpu()
    {
        if (!gpuAttempted)
        {
            gpuAttempted = true;
            uint opened = PdhOpenQueryW(null, UIntPtr.Zero, out query);
            if (opened != 0) { CloseQuery(); gpuStatus = $"GPU counters unavailable (0x{opened:X8})"; return []; }
            uint added = PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out gpuCounter);
            if (added != 0) { CloseQuery(); gpuStatus = $"GPU counters unavailable (0x{added:X8})"; return []; }
            PdhCollectQueryData(query); return [];
        }
        if (query == IntPtr.Zero || gpuCounter == IntPtr.Zero) return [];
        uint collected = PdhCollectQueryData(query);
        if (collected != 0) { gpuStatus = $"GPU sample unavailable (0x{collected:X8})"; return []; }
        // Read the documented wildcard array, with a bounded allocation and retries
        // for instances which start/exit between the sizing and reading calls.
        for (int retry = 0; retry < 3; retry++)
        {
            uint bytes = 0, count;
            uint sized = PdhGetFormattedCounterArrayW(gpuCounter, 0x200, ref bytes, out count, IntPtr.Zero);
            if (sized != 0x800007D2 || bytes is 0 or > 4194304) { gpuStatus = "GPU process sample unavailable"; return []; }
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                uint capacity = bytes;
                uint read = PdhGetFormattedCounterArrayW(gpuCounter, 0x200, ref capacity, out count, buffer);
                if (read == 0x800007D2) continue;
                if (read != 0) { gpuStatus = $"GPU sample unavailable (0x{read:X8})"; return []; }
                int stride = Marshal.SizeOf<CounterItem>();
                if ((ulong)count * (uint)stride > bytes) throw new InvalidOperationException("Invalid GPU counter buffer.");
                var results = new List<GpuEngineSample>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, i * stride));
                    if (item.Status is not (0 or 1) || item.Name == IntPtr.Zero) continue;
                    string name = Marshal.PtrToStringUni(item.Name) ?? "";
                    if (TryParseInstance(name, out int pid, out string engine)) results.Add(new(pid, engine, item.Value));
                }
                gpuStatus = "GPU: busiest engine / all adapters"; return results;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        gpuStatus = "GPU counters changing; waiting for next sample"; return [];
    }
    internal static bool TryParseInstance(string name, out int pid, out string engine)
    {
        pid = 0; engine = "";
        if (!name.StartsWith("pid_", StringComparison.Ordinal)) return false;
        int end = name.IndexOf('_', 4), marker = name.IndexOf("_engtype_", StringComparison.Ordinal);
        if (end <= 4 || marker < 0 || !int.TryParse(name.AsSpan(4, end - 4), out pid) || pid <= 0) return false;
        engine = name[(marker + 9)..]; return engine.Length > 0;
    }
    // PDH_FMT_COUNTERVALUE_ITEM_W, x64: pointer, status DWORD, aligned double union.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CounterItem { [FieldOffset(0)] public IntPtr Name; [FieldOffset(8)] public uint Status; [FieldOffset(16)] public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint PdhOpenQueryW(string? dataSource, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhCloseQuery(IntPtr query);
    private void CloseQuery() { if (query != IntPtr.Zero) PdhCloseQuery(query); query = gpuCounter = IntPtr.Zero; }
    public void Dispose() { CloseQuery(); previous = []; }
}
