using LibreHardwareMonitor.Hardware;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class Monitoring : IDisposable
{
    private readonly Computer computer = new() { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };
    public Monitoring()
    {
        try { computer.Open(); }
        catch { computer.Close(); throw; }
    }
    public Telemetry Read()
    {
        var sensors = new List<(HardwareType Kind, ISensor Sensor)>();
        void Visit(IHardware h)
        {
            h.Update(); foreach (var s in h.Sensors) sensors.Add((h.HardwareType, s));
            foreach (var sub in h.SubHardware) Visit(sub);
        }
        foreach (var h in computer.Hardware) Visit(h);
        double? Maximum(Func<(HardwareType Kind, ISensor Sensor), bool> predicate)
        {
            var a = sensors.Where(predicate).Where(x => x.Sensor.Value.HasValue && float.IsFinite(x.Sensor.Value.Value)).Select(x => (double)x.Sensor.Value!.Value).ToArray();
            return a.Length == 0 ? null : a.Max();
        }
        var cpuRaw = Maximum(x => x.Kind == HardwareType.Cpu && x.Sensor.SensorType == SensorType.Temperature);
        static bool IsGpuTemperature((HardwareType Kind, ISensor Sensor) x) => x.Kind is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel && x.Sensor.SensorType == SensorType.Temperature;
        var gpuRaw = Maximum(x => IsGpuTemperature(x) && x.Sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
            ?? Maximum(x => IsGpuTemperature(x) && !x.Sensor.Name.Contains("Hot", StringComparison.OrdinalIgnoreCase) && !x.Sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase));
        var gpuHotSpot = Maximum(x => IsGpuTemperature(x) && x.Sensor.Name.Contains("Hot", StringComparison.OrdinalIgnoreCase));
        var gpuMemory = Maximum(x => IsGpuTemperature(x) && x.Sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase));
        // Unsupported kernel access can expose a zero-valued sentinel sensor.
        // Never present that as a real reading or silently enable a normal fan curve.
        double? cpu = cpuRaw is >= 5 and <= 115 ? cpuRaw : null;
        double? gpu = gpuRaw is >= 5 and <= 115 ? gpuRaw : null;
        var cpuLoad = Maximum(x => x.Kind == HardwareType.Cpu && x.Sensor.SensorType == SensorType.Load && x.Sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase));
        var gpuLoad = Maximum(x => x.Kind is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel && x.Sensor.SensorType == SensorType.Load && x.Sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));
        var mem = Maximum(x => x.Kind == HardwareType.Memory && x.Sensor.SensorType == SensorType.Load);
        return new(DateTimeOffset.UtcNow, cpu, gpu, cpuLoad, gpuLoad, mem,
            cpu.HasValue ? "" : "CPU temperature unavailable. Check supported LHM/PawnIO setup; cooling falls back to 100%.")
        { GpuHotSpotTemperature = gpuHotSpot, GpuMemoryTemperature = gpuMemory };
    }
    public void Dispose() => computer.Close();
}
