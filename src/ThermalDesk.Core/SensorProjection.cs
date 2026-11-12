namespace ThermalDesk.Core;

public enum DeviceCategory { Cpu, NvidiaGpu, AmdGpu, IntelGpu, Memory }
public enum GpuAdapterKind { Unknown, Integrated, Discrete }
public enum SensorMetric { Temperature, Load, ClockMhz, DataGiB, PowerWatts }
public sealed record SensorReading(string Name, SensorMetric Metric, double? Value);
public sealed record HardwareSnapshot(string Id, string Name, DeviceCategory Category, IReadOnlyList<SensorReading> Sensors)
{ public GpuAdapterKind AdapterKind { get; init; } }

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
        // Package counters already include cores/SoC on supported hardware. Never
        // sum package + per-core power or use TDP / load as a power measurement.
        var cpuPackages = devices.Where(d => d.Category == DeviceCategory.Cpu).Select(d => Best(d.Sensors, SensorMetric.PowerWatts,
            n => n.Equals("Package", StringComparison.OrdinalIgnoreCase) || n.Equals("CPU Package", StringComparison.OrdinalIgnoreCase), 0, 2000)).ToArray();
        double? cpuPower = cpuPackages.Length > 0 && cpuPackages.All(p => p is not null) ? cpuPackages.Sum(p => p!.Value) : null;
        // Nominal sampled core clocks, not BCLK / fabric / SoC / effective-clock averages.
        var cpuClocks = cpuSensors.Where(s => s.Metric == SensorMetric.ClockMhz && Has(s.Name, "Core") &&
            !Has(s.Name, "Effective") && !Has(s.Name, "Bus") && !Has(s.Name, "Fabric") && !Has(s.Name, "SoC") &&
            s.Value.HasValue && double.IsFinite(s.Value.Value) && s.Value.Value is >= 0 and <= 15000).Select(s => s.Value!.Value).ToArray();
        // Never rank by GPU vendor, temperature or momentary activity. Explicit
        // discrete/integrated information wins; otherwise use reported dedicated
        // capacity, then a stable identifier. Unknown UMA devices remain unknown.
        double Capacity(HardwareSnapshot d) => Best(d.Sensors, SensorMetric.DataGiB,
            n => n.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase) || n.Equals("D3D Dedicated Memory Total", StringComparison.OrdinalIgnoreCase), .001, 1024)?.Value ?? 0;
        var gpuDevices = devices.Where(IsGpu).OrderByDescending(d => d.AdapterKind == GpuAdapterKind.Integrated ? 0 : 1)
            .ThenByDescending(Capacity).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
        var gpu = preferredGpuId.Length > 0 ? gpuDevices.FirstOrDefault(d => d.Id == preferredGpuId) : gpuDevices.FirstOrDefault();
        var gpuSensors = gpu?.Sensors ?? [];
        static bool Named(string name, params string[] aliases) => aliases.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);
        var core = Best(gpuSensors, SensorMetric.Temperature, n => Named(n, "GPU Core", "GPU Temperature", "GPU"), 5, 115);
        var hot = Best(gpuSensors, SensorMetric.Temperature, n => Has(n, "Hot"), 5, 125);
        var videoMemory = Best(gpuSensors, SensorMetric.Temperature, n => Has(n, "Memory"), 5, 125);
        var gpuLoad = Best(gpuSensors, SensorMetric.Load, n => Named(n, "GPU Core", "GPU Total", "GPU Load", "GPU Utilization"), 0, 100);
        bool engineLoad = gpuLoad is null;
        gpuLoad ??= Best(gpuSensors, SensorMetric.Load, n =>
        { string engine = n.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase) ? n[4..] : n; return Has(engine, "3D") || Has(engine, "Compute"); }, 0, 100);
        var gpuCoreClock = Best(gpuSensors, SensorMetric.ClockMhz, n => Named(n, "GPU Core", "GPU Clock", "GPU Frequency"), 0, 15000);
        var gpuMemoryClock = Best(gpuSensors, SensorMetric.ClockMhz, n => Named(n, "GPU Memory", "GPU Memory Clock"), 0, 50000);
        var gpuPower = Best(gpuSensors, SensorMetric.PowerWatts, n => Named(n, "GPU Total", "GPU Board Power"), 0, 2000)
            ?? Best(gpuSensors, SensorMetric.PowerWatts, n => Named(n, "GPU Package"), 0, 2000);
        // Exact dedicated VRAM names avoid mistaking memory-controller utilization or shared RAM for VRAM capacity.
        var vramUsed = Best(gpuSensors, SensorMetric.DataGiB, n => n.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase), 0, 1024);
        var vramTotal = Best(gpuSensors, SensorMetric.DataGiB, n => n.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase), .001, 1024);
        string memoryProvider = "Native dedicated GPU memory";
        if (vramUsed is null || vramTotal is null || vramUsed.Value > vramTotal.Value)
        {
            var used = Best(gpuSensors, SensorMetric.DataGiB, n => Named(n, "D3D Dedicated Memory Used"), 0, 1024);
            var total = Best(gpuSensors, SensorMetric.DataGiB, n => Named(n, "D3D Dedicated Memory Total"), .001, 1024);
            if (used is not null && total is not null && used.Value <= total.Value)
            { vramUsed = used; vramTotal = total; memoryProvider = "Windows D3D dedicated GPU memory"; }
            // An iGPU can expose dedicated allocation usage without a capacity.
            else if (vramUsed is null) { vramUsed = used; memoryProvider = "Windows D3D dedicated allocation (capacity unavailable)"; }
        }
        bool validVramPair = vramUsed is not null && vramTotal is not null && vramUsed.Value <= vramTotal.Value;
        var sharedUsed = Best(gpuSensors, SensorMetric.DataGiB, n => Named(n, "D3D Shared Memory Used", "GPU Shared Memory Used"), 0, 4096);
        var sharedTotal = Best(gpuSensors, SensorMetric.DataGiB, n => Named(n, "D3D Shared Memory Total", "GPU Shared Memory Total"), .001, 4096);
        var memory = Best(devices.Where(d => d.Category == DeviceCategory.Memory).SelectMany(d => d.Sensors), SensorMetric.Load,
            n => !Has(n, "Virtual") && (n.Equals("Memory", StringComparison.OrdinalIgnoreCase) || Has(n, "Physical")), 0, 100);
        var warnings = new List<string>();
        if (cpu is null) warnings.Add("CPU temperature unavailable. Hardware/driver access may not expose it; check supported LHM/PawnIO setup. Missing data alone never forces 100%; genuinely critical heat is a separate condition.");
        if (gpu is null && preferredGpuId.Length > 0) warnings.Add("Selected GPU unavailable; no other GPU was substituted.");
        else if (gpu is not null && core is null) warnings.Add("GPU core temperature unavailable on the selected device.");
        return new(time, cpu?.Value, core?.Value, cpuLoad?.Value, gpuLoad?.Value, memory?.Value, string.Join(" ", warnings))
        {
            GpuHotSpotTemperature = hot?.Value, GpuMemoryTemperature = videoMemory?.Value,
            CpuMaximumTemperature = cpuMaximum?.Value, CpuMaximumTemperatureSource = cpuMaximum?.Name ?? "",
            GpuVramUsedGiB = vramUsed?.Value, GpuVramTotalGiB = vramTotal?.Value,
            GpuVramUsedPercent = validVramPair ? vramUsed!.Value / vramTotal!.Value * 100 : null,
            GpuSharedMemoryUsedGiB = sharedUsed?.Value, GpuSharedMemoryTotalGiB = sharedTotal?.Value,
            GpuAdapterKind = gpu?.AdapterKind ?? GpuAdapterKind.Unknown,
            GpuMemorySource = vramUsed is not null || vramTotal is not null ? memoryProvider : "Dedicated memory unavailable; shared RAM is separate",
            GpuLoadSource = gpuLoad is null ? "" : gpuLoad.Name + (engineLoad ? " (busiest 3D/compute engine, not summed)" : ""),
            CpuDeviceName = string.Join(" + ", devices.Where(d => d.Category == DeviceCategory.Cpu).Select(d => d.Name)),
            CpuLoadSource = cpuLoad?.Name ?? "",
            CpuClockAverageMhz = cpuClocks.Length > 0 ? cpuClocks.Average() : null,
            CpuClockMaximumMhz = cpuClocks.Length > 0 ? cpuClocks.Max() : null,
            CpuPowerWatts = cpuPower, GpuPowerWatts = gpuPower?.Value,
            CpuPowerSource = cpuPower.HasValue ? string.Join(" + ", cpuPackages.Select(p => p!.Name)) : "Package power unavailable",
            GpuPowerSource = gpuPower?.Name ?? "Package/board power unavailable",
            GpuCoreClockMhz = gpuCoreClock?.Value, GpuMemoryClockMhz = gpuMemoryClock?.Value,
            CpuTemperatureSource = cpu?.Name ?? "", GpuTemperatureSource = core?.Name ?? "", MemoryLoadSource = memory?.Name ?? "",
            GpuDeviceId = gpu?.Id ?? preferredGpuId, GpuDeviceName = gpu?.Name ?? "", GpuDevices = gpuDevices.Select(d => new GpuDeviceInfo(d.Id, d.Name) { Kind = d.AdapterKind }).ToArray()
        };
    }
}
