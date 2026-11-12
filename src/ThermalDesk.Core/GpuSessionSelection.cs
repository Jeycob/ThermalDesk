namespace ThermalDesk.Core;

// One instance per monitoring session. Never silently rebind a running curve to
// another adapter when a device disappears or its driver stops reporting.
public sealed class GpuSessionSelection
{
    private string automaticId = "", previousPreference = "";
    public Telemetry Project(IReadOnlyList<HardwareSnapshot> devices, DateTimeOffset time, string preferredId)
    {
        if (previousPreference != preferredId) { automaticId = ""; previousPreference = preferredId; }
        var result = SensorProjection.Project(devices, time, preferredId.Length > 0 ? preferredId : automaticId);
        if (preferredId.Length == 0 && automaticId.Length == 0 && result.GpuDeviceName.Length > 0) automaticId = result.GpuDeviceId;
        return result;
    }
}
