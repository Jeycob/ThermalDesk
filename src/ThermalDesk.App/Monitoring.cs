using LibreHardwareMonitor.Hardware;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class Monitoring : IDisposable
{
    private readonly Computer computer = new() { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };
    private readonly WindowsSystemMonitor fallback = new();
    private readonly bool opened;
    private readonly string initializationWarning = "";
    private readonly GpuSessionSelection selection = new();
    private readonly string cpuName;
    public Monitoring()
    {
        cpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";
        try { cpuName = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string ?? cpuName; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        try { computer.Open(); opened = true; }
        catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException)
        { initializationWarning = "Hardware sensor initialization unavailable · " + e.GetType().Name; try { computer.Close(); } catch { } }
    }
    public Telemetry Read(string preferredGpuId = "")
    {
        var devices = new List<HardwareSnapshot>(); var warnings = new List<string>();
        var sampleTime = DateTimeOffset.UtcNow;
        void Visit(IHardware h, List<SensorReading> sensors)
        {
            h.Update();
            foreach (var s in h.Sensors)
            {
                if (NormalizeReading(s.Name, s.SensorType, s.Value) is { } reading) sensors.Add(reading);
            }
            foreach (var sub in h.SubHardware) Visit(sub, sensors);
        }
        foreach (var h in opened ? computer.Hardware : [])
        {
            DeviceCategory? kind = h.HardwareType switch
            {
                HardwareType.Cpu => DeviceCategory.Cpu, HardwareType.GpuNvidia => DeviceCategory.NvidiaGpu,
                HardwareType.GpuAmd => DeviceCategory.AmdGpu, HardwareType.GpuIntel => DeviceCategory.IntelGpu,
                HardwareType.Memory => DeviceCategory.Memory, _ => null
            };
            if (kind is null) continue;
            var sensors = new List<SensorReading>();
            try { Visit(h, sensors); }
            catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException)
            { warnings.Add(h.Name + " sensor read failed · " + e.GetType().Name); continue; }
            devices.Add(new(h.Identifier.ToString(), h.Name, kind.Value, sensors)
            {
                // Pinned LHM provider types identify Intel's integrated/discrete
                // adapters; unknown AMD/NVIDIA devices are not guessed by model name.
                AdapterKind = h.GetType().Name switch { "IntelIntegratedGpu" => GpuAdapterKind.Integrated, "IntelDiscreteGpu" => GpuAdapterKind.Discrete, _ => GpuAdapterKind.Unknown }
            });
        }
        var result = selection.Project(devices, sampleTime, preferredGpuId);
        var system = fallback.Read();
        if (initializationWarning.Length > 0) warnings.Add(initializationWarning);
        if (result.Warning.Length > 0) warnings.Add(result.Warning);
        return result with
        {
            CpuLoad = result.CpuLoad ?? system.CpuLoad,
            CpuLoadSource = result.CpuLoad.HasValue ? result.CpuLoadSource : system.CpuLoad.HasValue ? "Windows GetSystemTimes (whole PC)" : "",
            MemoryUsedPercent = result.MemoryUsedPercent ?? system.MemoryLoad,
            MemoryLoadSource = result.MemoryUsedPercent.HasValue ? result.MemoryLoadSource : system.MemoryLoad.HasValue ? "Windows physical memory" : "",
            CpuDeviceName = result.CpuDeviceName.Length > 0 ? result.CpuDeviceName : cpuName.Trim(), Warning = string.Join(" ", warnings)
        };
    }
    internal static SensorReading? NormalizeReading(string name, SensorType type, float? value)
    {
        // Units are pinned to LibreHardwareMonitor 0.9.6: Clock=MHz, Data=GiB, SmallData=MiB.
        // Do not infer units from magnitude; a future library upgrade must review this conversion.
        SensorMetric? metric = type switch
        {
            SensorType.Temperature => SensorMetric.Temperature, SensorType.Load => SensorMetric.Load,
            SensorType.Clock => SensorMetric.ClockMhz, SensorType.Power => SensorMetric.PowerWatts,
            SensorType.Data or SensorType.SmallData => SensorMetric.DataGiB, _ => null
        };
        return metric.HasValue ? new(name, metric.Value, type == SensorType.SmallData ? value / 1024d : value) : null;
    }
    public void Dispose() { if (opened) computer.Close(); }
}
