namespace ThermalDesk.Core;

public enum DeviceCategory { Cpu, NvidiaGpu, AmdGpu, IntelGpu, Memory }
public enum SensorMetric { Temperature, Load }
public sealed record SensorReading(string Name, SensorMetric Metric, double? Value);
public sealed record HardwareSnapshot(string Id, string Name, DeviceCategory Category, IReadOnlyList<SensorReading> Sensors);

// Pure projection: no driver calls. All GPU values come from ONE selected device;
// a hotter integrated GPU must not silently become the RTX temperature or load.
public static class SensorProjection
{
    public static Telemetry Project(IReadOnlyList<HardwareSnapshot> devices, DateTimeOffset time, string preferredGpuId = "")
    {
        static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);
        static bool IsGpu(HardwareSnapshot d) => d.Category is DeviceCategory.NvidiaGpu or DeviceCategory.AmdGpu or DeviceCategory.IntelGpu;
        static SensorReading? Best(IEnumerable<SensorReading> sensors, SensorMetric metric, Func<string, bool> name, double low, double high) =>
            sensors.Where(s => s.Metric == metric && name(s.Name) && s.Value.HasValue && double.IsFinite(s.Value.Value) && s.Value.Value >= low && s.Value.Value <= high)
                .OrderByDescending(s => s.Value).FirstOrDefault();
        var cpuSensors = devices.Where(d => d.Category == DeviceCategory.Cpu).SelectMany(d => d.Sensors).ToArray();
        var cpuTemperatures = cpuSensors.Where(s => !Has(s.Name, "Distance") && !Has(s.Name, "Margin") && !Has(s.Name, "TjMax")).ToArray();
        var cpu = Best(cpuTemperatures, SensorMetric.Temperature, n => Has(n, "Tctl/Tdie") || Has(n, "Package"), 5, 115)
            ?? Best(cpuTemperatures, SensorMetric.Temperature, n => Has(n, "Tctl") || Has(n, "Tdie") && !Has(n, "CCD"), 5, 115)
            ?? Best(cpuTemperatures, SensorMetric.Temperature, _ => true, 5, 115);
        var cpuLoad = Best(cpuSensors, SensorMetric.Load, n => Has(n, "Total"), 0, 100);
        var cpuMaximum = Best(cpuTemperatures, SensorMetric.Temperature, _ => true, 5, 115);
        var gpuDevices = devices.Where(IsGpu).OrderBy(d => d.Category).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
        var gpu = preferredGpuId.Length > 0 ? gpuDevices.FirstOrDefault(d => d.Id == preferredGpuId) : gpuDevices.FirstOrDefault();
        var gpuSensors = gpu?.Sensors ?? [];
        var core = Best(gpuSensors, SensorMetric.Temperature, n => Has(n, "Core"), 5, 115)
            ?? Best(gpuSensors, SensorMetric.Temperature, n => !Has(n, "Hot") && !Has(n, "Memory"), 5, 115);
        var hot = Best(gpuSensors, SensorMetric.Temperature, n => Has(n, "Hot"), 5, 125);
        var videoMemory = Best(gpuSensors, SensorMetric.Temperature, n => Has(n, "Memory"), 5, 125);
        var gpuLoad = Best(gpuSensors, SensorMetric.Load, n => Has(n, "Core") || Has(n, "GPU Total") || n.Equals("GPU Load", StringComparison.OrdinalIgnoreCase), 0, 100);
        var memory = Best(devices.Where(d => d.Category == DeviceCategory.Memory).SelectMany(d => d.Sensors), SensorMetric.Load,
            n => !Has(n, "Virtual") && (n.Equals("Memory", StringComparison.OrdinalIgnoreCase) || Has(n, "Physical")), 0, 100);
        var warnings = new List<string>();
        if (cpu is null) warnings.Add("CPU temperature unavailable. Check supported LHM/PawnIO setup; control is not taken over or is released, never forced to 100%.");
        if (gpu is null && preferredGpuId.Length > 0) warnings.Add("Selected GPU unavailable; no other GPU was substituted.");
        else if (gpu is not null && core is null) warnings.Add("GPU core temperature unavailable on the selected device.");
        return new(time, cpu?.Value, core?.Value, cpuLoad?.Value, gpuLoad?.Value, memory?.Value, string.Join(" ", warnings))
        {
            GpuHotSpotTemperature = hot?.Value, GpuMemoryTemperature = videoMemory?.Value,
            CpuMaximumTemperature = cpuMaximum?.Value, CpuMaximumTemperatureSource = cpuMaximum?.Name ?? "",
            CpuTemperatureSource = cpu?.Name ?? "", GpuTemperatureSource = core?.Name ?? "", MemoryLoadSource = memory?.Name ?? "",
            GpuDeviceId = gpu?.Id ?? preferredGpuId, GpuDeviceName = gpu?.Name ?? "", GpuDevices = gpuDevices.Select(d => new GpuDeviceInfo(d.Id, d.Name)).ToArray()
        };
    }
}
