using System.Diagnostics;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal interface IGpuProcessQuery : IDisposable
{
    IReadOnlyList<GpuEngineSample> Read();
}

// One query at a time, with bounded retries after driver/counter initialization
// or sampling failures. A failed query never supplies its previous readings.
internal sealed class GpuProcessSampler : IDisposable
{
    private readonly Func<IGpuProcessQuery> open;
    private readonly Func<double> seconds;
    private IGpuProcessQuery? query;
    private double retryAt;
    private int failures;
    private bool disposed;
    internal string Status { get; private set; } = "GPU warming up";

    internal GpuProcessSampler(Func<IGpuProcessQuery>? open = null, Func<double>? seconds = null)
    {
        this.open = open ?? (() => new NativeGpuProcessQuery());
        this.seconds = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }

    internal IReadOnlyList<GpuEngineSample> Read()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (query is null && seconds() < retryAt) return [];
        try
        {
            if (query is null)
            {
                query = open();
                Status = "GPU warming up";
                return [];
            }
            var values = query.Read();
            failures = 0;
            Status = "GPU: busiest engine / all adapters";
            return values;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            query?.Dispose(); query = null;
            failures = Math.Min(failures + 1, 5);
            int delay = Math.Min(60, 5 << (failures - 1));
            retryAt = seconds() + delay;
            string cause = error is System.ComponentModel.Win32Exception native ? $" (0x{native.NativeErrorCode:X8})" : "";
            Status = $"GPU process counters unavailable{cause} · retry in {delay}s";
            return [];
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; query?.Dispose(); query = null;
    }
}
