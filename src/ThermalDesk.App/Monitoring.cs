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
    public Telemetry Read(string preferredGpuId = "")
    {
        var devices = new List<HardwareSnapshot>();
        void Visit(IHardware h, List<SensorReading> sensors)
        {
            h.Update();
            foreach (var s in h.Sensors)
                if (s.SensorType is SensorType.Temperature or SensorType.Load)
                    sensors.Add(new(s.Name, s.SensorType == SensorType.Temperature ? SensorMetric.Temperature : SensorMetric.Load, s.Value));
            foreach (var sub in h.SubHardware) Visit(sub, sensors);
        }
        foreach (var h in computer.Hardware)
        {
            DeviceCategory? kind = h.HardwareType switch
            {
                HardwareType.Cpu => DeviceCategory.Cpu, HardwareType.GpuNvidia => DeviceCategory.NvidiaGpu,
                HardwareType.GpuAmd => DeviceCategory.AmdGpu, HardwareType.GpuIntel => DeviceCategory.IntelGpu,
                HardwareType.Memory => DeviceCategory.Memory, _ => null
            };
            if (kind is null) continue;
            var sensors = new List<SensorReading>(); Visit(h, sensors);
            devices.Add(new(h.Identifier.ToString(), h.Name, kind.Value, sensors));
        }
        return SensorProjection.Project(devices, DateTimeOffset.UtcNow, preferredGpuId);
    }
    public void Dispose() => computer.Close();
}
