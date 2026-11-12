using ThermalDesk.Core;

internal static class UniversalTelemetryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (string cpu in new[] { "Intel Core", "AMD Ryzen" })
        foreach (var gpu in new[] { DeviceCategory.IntelGpu, DeviceCategory.AmdGpu, DeviceCategory.NvidiaGpu })
        {
            var projected = SensorProjection.Project([
                new("cpu", cpu, DeviceCategory.Cpu, [new(cpu.StartsWith("Intel") ? "CPU Package" : "Core (Tctl/Tdie)", SensorMetric.Temperature, 50), new("CPU Total", SensorMetric.Load, 20), new("Core #1", SensorMetric.ClockMhz, 4000), new("Core #2", SensorMetric.ClockMhz, 5000)]),
                new("gpu", gpu.ToString(), gpu, [new("GPU Core", SensorMetric.Temperature, 60), new("GPU Core", SensorMetric.Load, 40), new("GPU Core", SensorMetric.ClockMhz, 2000), new("GPU Memory", SensorMetric.ClockMhz, 6000), new("GPU Memory Used", SensorMetric.DataGiB, 4), new("GPU Memory Total", SensorMetric.DataGiB, 8)])], now);
            check(projected.CpuTemperature == 50 && projected.CpuLoad == 20 && projected.CpuClockAverageMhz == 4500, "CPU normal form: " + cpu + "/" + gpu);
            check(projected.GpuTemperature == 60 && projected.GpuLoad == 40 && projected.GpuCoreClockMhz == 2000 && projected.GpuMemoryClockMhz == 6000 && projected.GpuVramUsedPercent == 50, "GPU normal form: " + cpu + "/" + gpu);
        }
        HardwareSnapshot Gpu(string id, DeviceCategory vendor, double total, GpuAdapterKind kind = GpuAdapterKind.Unknown) => new(id, id, vendor,
            [new("GPU Core", SensorMetric.Temperature, 40), new("GPU Memory Total", SensorMetric.DataGiB, total)]) { AdapterKind = kind };
        HardwareSnapshot[] mixed = [Gpu("nvidia", DeviceCategory.NvidiaGpu, 16), Gpu("amd", DeviceCategory.AmdGpu, 24), Gpu("intel-arc", DeviceCategory.IntelGpu, 8, GpuAdapterKind.Discrete), Gpu("intel-igpu", DeviceCategory.IntelGpu, 32, GpuAdapterKind.Integrated)];
        check(SensorProjection.Project(mixed, now).GpuDeviceId == "amd", "larger AMD dedicated capacity beats NVIDIA and known integrated memory");
        check(SensorProjection.Project(mixed.Reverse().ToArray(), now).GpuDeviceId == "amd", "automatic selection independent of enumeration order");
        var session = new GpuSessionSelection(); session.Project(mixed, now, "");
        var missing = session.Project(mixed.Where(g => g.Id != "amd").ToArray(), now, "");
        check(missing.GpuDeviceId == "amd" && missing.GpuTemperature is null && missing.GpuLoad is null, "automatic session selection does not silently switch after device loss");
        check(session.Project(mixed, now, "intel-arc").GpuDeviceId == "intel-arc", "explicit pin overrides automatic preference");
        check(session.Project(mixed, now, "").GpuDeviceId == "amd", "explicit return to automatic recomputes selection");
        var igpu = SensorProjection.Project([new("intel-igpu", "Intel integrated", DeviceCategory.IntelGpu,
            [new("D3D 3D", SensorMetric.Load, 20), new("D3D Compute_0", SensorMetric.Load, 70), new("D3D Copy", SensorMetric.Load, 99), new("D3D Shared Memory Used", SensorMetric.DataGiB, 3), new("D3D Shared Memory Total", SensorMetric.DataGiB, 32)]) { AdapterKind = GpuAdapterKind.Integrated }], now);
        check(igpu.GpuLoad == 70 && igpu.GpuLoadSource.Contains("not summed"), "iGPU busiest compute/render engine, not copy load or sum");
        check(igpu.GpuTemperature is null && igpu.GpuVramTotalGiB is null && igpu.GpuVramUsedPercent is null && igpu.GpuSharedMemoryUsedGiB == 3, "iGPU shared RAM never fabricated as dedicated VRAM/temperature");
        var amd = SensorProjection.Project([new("amd", "AMD", DeviceCategory.AmdGpu,
            [new("GPU VR VDDC", SensorMetric.Temperature, 110), new("GPU Liquid", SensorMetric.Temperature, 35), new("GPU Hot Spot", SensorMetric.Temperature, 100), new("D3D Dedicated Memory Used", SensorMetric.DataGiB, 4), new("D3D Dedicated Memory Total", SensorMetric.DataGiB, 16)])], now);
        check(amd.GpuTemperature is null && amd.GpuHotSpotTemperature == 100, "VRM/liquid/hotspot cannot masquerade as core temperature");
        check(amd.GpuVramUsedPercent == 25 && amd.GpuMemorySource.Contains("D3D"), "AMD D3D dedicated-memory fallback normalized");
        check(!FanSafety.IsCritical(amd with { CpuTemperature = 50 }, now), "non-core GPU temperature never triggers core critical threshold");
        check(SystemLoadProjection.CpuLoad(new(100, 200, 100), new(140, 260, 140)) == 60, "OS CPU fallback subtracts idle already included in kernel");
        check(SystemLoadProjection.CpuLoad(new(100, 200, 100), new(100, 200, 100)) is null && SystemLoadProjection.CpuLoad(new(100, 200, 100), new(500, 260, 140)) is null, "zero/reset OS counters stay unavailable");
        check(SystemLoadProjection.PhysicalMemoryLoad(100, 40) == 60 && SystemLoadProjection.PhysicalMemoryLoad(0, 0) is null && SystemLoadProjection.PhysicalMemoryLoad(100, 101) is null, "OS physical memory fallback range checked");
        var settings = new Settings { AllowMonitoringOnly = true }; settings.ValidateLiveSelection();
        check(settings.HasLiveSelection && !settings.HasUsbSelection && settings.Copy().AllowMonitoringOnly, "monitoring-only is explicit, persistent and does not select USB devices");
    }
}
