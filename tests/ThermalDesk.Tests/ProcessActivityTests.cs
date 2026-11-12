using ThermalDesk.Core;

static class ProcessActivityTests
{
    internal static void Run(Action<bool, string> check)
    {
        ProcessCpuSample[] before = [new(10, 1, "editor", 10), new(20, 2, "worker", 100), new(30, 1, "reused", 1)];
        ProcessCpuSample[] after = [new(10, 1, "editor", 11), new(20, 2, "worker", 104), new(30, 2, "different", 10), new(40, 1, "new", 20)];
        var cpu = ProcessActivityProjection.TopCpu(before, after, 2, 4);
        check(cpu is { Pid: 20, Percent: 50 }, "CPU delta normalized across logical processors");
        check(ProcessActivityProjection.TopCpu(before, after, 0, 4) is null && ProcessActivityProjection.TopCpu(before, after, 2, 0) is null, "invalid sample period/core count rejected");
        check(ProcessActivityProjection.TopCpu(before, [new(30, 2, "reused", 10)], 2, 4) is null, "PID reuse never inherits old CPU history");
        check(ProcessActivityProjection.TopCpu(before, [new(10, 1, "counter reset", 1)], 2, 4) is null, "negative CPU delta is not utilization");
        check(ProcessActivityProjection.TopCpu([], after, 5, 4) is null, "first CPU sample warms up, not invented 0 percent");
        var gpu = ProcessActivityProjection.TopGpu([new(10, "3D", 60), new(10, "Copy", 60), new(20, "Compute", 80), new(999, "gone", 99)], after);
        check(gpu is { Pid: 20, Percent: 80, Engine: "Compute" }, "GPU ranks busiest engine, not overlapping engine sum");
        check(ProcessActivityProjection.TopGpu([new(10, "3D", double.NaN), new(20, "3D", -2)], after) is null, "non-finite/negative GPU values rejected");
        var sample = new Telemetry(DateTimeOffset.UtcNow, null, null, null, null, null) { TopCpuProcess = cpu, TopGpuProcess = gpu };
        var widget = new LcdWidget { Source = WidgetSource.TopCpuProcess, TextTemplate = "CPU {value}" };
        check(WidgetLayout.Format(widget, sample) == "CPU worker [20] 50.0%", "LCD process text includes name, PID and percentage");
        check(WidgetLayout.Format(widget, sample with { TopCpuProcess = null }) == "CPU —", "unavailable process is a dash, not stale data");
        var profile = new Settings { MinimizeAfterStartup = false, MinimizeWhenClosed = true, ProcessMonitorEnabled = true, ProcessSampleSeconds = 15 };
        var copy = profile.Copy();
        check(copy.MinimizeAfterStartup == false && copy.MinimizeWhenClosed && copy.ProcessMonitorEnabled && copy.ProcessSampleSeconds == 15, "window and process preferences round-trip through profiles");
        check(new Settings().MinimizeAfterStartup is null && !new Settings().ProcessMonitorEnabled, "legacy minimize migration and optional process monitoring default off");
        bool rejected = false; try { new Settings { ProcessSampleSeconds = 1 }.Validate(); } catch (ArgumentException) { rejected = true; }
        check(rejected, "process scan cannot be configured faster than five seconds");
    }
}
