using System.Diagnostics;
using System.Text.Json.Serialization;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// One writer (the sensor thread), immutable snapshots for every reader. These
// observations never decide freshness, add retries, or interrupt a driver call.
internal sealed record SamplerProbe
{
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public long IterationId { get; init; }
    public string Phase { get; init; } = "NotStarted";
    public string Device { get; init; } = "";
    public string Provider { get; init; } = "";
    public bool IterationInProgress { get; init; }
    public DateTimeOffset? IterationStartedUtc { get; init; }
    public long? IterationStartedTimestamp { get; init; }
    public long? PhaseStartedTimestamp { get; init; }
    public DateTimeOffset? MeasurementStartedUtc { get; init; }
    public long? MeasurementStartedTimestamp { get; init; }
    public double? CurrentStartToStartGapMilliseconds { get; init; }
    public long? PreviousCompletedIterationId { get; init; }
    public double? PreviousCompletedReadMilliseconds { get; init; }
    public long? LastPublishedIterationId { get; init; }
    public DateTimeOffset? LastPublishedSampleTime { get; init; }
    public DateTimeOffset? LastPublishedMeasurementStartedUtc { get; init; }
    public long? LastPublishedMeasurementStartedTimestamp { get; init; }
    public long? LastPublishedTimestamp { get; init; }
}

internal sealed record SamplerDiagnosticSnapshot(SamplerProbe Probe, DateTimeOffset ObservedUtc, long ObservedTimestamp)
{
    public long TimestampFrequency => Stopwatch.Frequency;
    public string Phase => Probe.Phase;
    public string ActiveDevice => Probe.Device;
    public string ActiveProvider => Probe.Provider;
    public double? CurrentIterationElapsedMilliseconds => Elapsed(Probe.IterationStartedTimestamp);
    public double? CurrentPhaseElapsedMilliseconds => Elapsed(Probe.PhaseStartedTimestamp);
    // Start of measurement, NOT publication: slow reads must remain visible.
    public double? LastPublishedMeasurementAgeMilliseconds => Elapsed(Probe.LastPublishedMeasurementStartedTimestamp);
    public double? LastPublicationAgeMilliseconds => Elapsed(Probe.LastPublishedTimestamp);
    public double? LastPublishedClockDifferenceMilliseconds => Probe.LastPublishedMeasurementStartedUtc is { } utc && LastPublishedMeasurementAgeMilliseconds is { } elapsed
        ? (ObservedUtc - utc).TotalMilliseconds - elapsed : null;
    public double? SampleTimeOffsetFromMeasurementStartMilliseconds => Probe.LastPublishedSampleTime is { } sample && Probe.LastPublishedMeasurementStartedUtc is { } start
        ? (sample - start).TotalMilliseconds : null;
    private double? Elapsed(long? start) => start.HasValue ? Stopwatch.GetElapsedTime(start.Value, ObservedTimestamp).TotalMilliseconds : null;
}

internal sealed class SamplerDiagnosticsRecorder
{
    private SamplerProbe probe = new();
    internal void Reset() => Volatile.Write(ref probe, new());
    internal SamplerDiagnosticSnapshot Snapshot()
    {
        var captured = Volatile.Read(ref probe);
        return new(captured, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
    }
    internal SamplerDiagnosticSnapshot Snapshot(DateTimeOffset utc, long timestamp) => new(Volatile.Read(ref probe), utc, timestamp);
    internal void BeginIteration(DateTimeOffset utc, long timestamp)
    {
        var previous = Volatile.Read(ref probe);
        Volatile.Write(ref probe, previous with
        {
            IterationId = previous.IterationId + 1, IterationInProgress = true,
            IterationStartedUtc = utc, IterationStartedTimestamp = timestamp,
            Phase = "IterationStart", PhaseStartedTimestamp = timestamp, Device = "", Provider = "",
            MeasurementStartedUtc = null, MeasurementStartedTimestamp = null,
            CurrentStartToStartGapMilliseconds = previous.IterationStartedTimestamp is { } last ? Stopwatch.GetElapsedTime(last, timestamp).TotalMilliseconds : null
        });
    }
    internal void EnterPhase(string phase, string device = "", string provider = "")
    {
        var previous = Volatile.Read(ref probe);
        Volatile.Write(ref probe, previous with { Phase = phase, PhaseStartedTimestamp = Stopwatch.GetTimestamp(), Device = device, Provider = provider });
    }
    internal void BeginMeasurement(DateTimeOffset utc, long timestamp)
    {
        var previous = Volatile.Read(ref probe);
        Volatile.Write(ref probe, previous with { Phase = "SensorRead", PhaseStartedTimestamp = timestamp, MeasurementStartedUtc = utc, MeasurementStartedTimestamp = timestamp, Device = "", Provider = "" });
    }
    internal void Published(Telemetry sample)
    {
        var previous = Volatile.Read(ref probe);
        long timestamp = Stopwatch.GetTimestamp();
        Volatile.Write(ref probe, previous with
        {
            Phase = "Published", PhaseStartedTimestamp = timestamp, Device = "", Provider = "",
            LastPublishedIterationId = previous.IterationId, LastPublishedSampleTime = sample.Time,
            LastPublishedMeasurementStartedUtc = previous.MeasurementStartedUtc,
            LastPublishedMeasurementStartedTimestamp = previous.MeasurementStartedTimestamp,
            LastPublishedTimestamp = timestamp
        });
    }
    internal void CompleteIteration()
    {
        var previous = Volatile.Read(ref probe);
        long timestamp = Stopwatch.GetTimestamp();
        Volatile.Write(ref probe, previous with
        {
            Phase = "Waiting", PhaseStartedTimestamp = timestamp, Device = "", Provider = "", IterationInProgress = false,
            PreviousCompletedIterationId = previous.IterationId,
            PreviousCompletedReadMilliseconds = previous.IterationStartedTimestamp is { } start ? Stopwatch.GetElapsedTime(start, timestamp).TotalMilliseconds : null
        });
    }
    internal void Stopped()
    {
        var previous = Volatile.Read(ref probe);
        Volatile.Write(ref probe, previous with { Phase = "Stopped", PhaseStartedTimestamp = Stopwatch.GetTimestamp(), Device = "", Provider = "", IterationInProgress = false });
    }
}

internal sealed record ControllerFaultEvidence(
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonIgnore] Telemetry Sample,
    [property: JsonPropertyName("sampler")] SamplerDiagnosticSnapshot Sampler,
    [property: JsonPropertyName("capturedAtDecision")] bool CapturedAtDecision)
{
    internal static ControllerFaultEvidence FromException(Exception error, Func<Telemetry> latest, Func<SamplerDiagnosticSnapshot> sampler)
    {
        if (error is TelemetryDecisionException decision) return decision.Evidence;
        return new(DateTimeOffset.UtcNow, error.Message, latest(), sampler(), false);
    }
    [JsonPropertyName("sampleTime")] public DateTimeOffset SampleTime => Sample.Time;
    [JsonPropertyName("sampleAgeSeconds")] public double SampleAgeSeconds => (Time - Sample.Time).TotalSeconds;
    [JsonPropertyName("lastReadMilliseconds")] public double LastReadMilliseconds => Sampler.Probe.PreviousCompletedReadMilliseconds ?? 0;
    [JsonPropertyName("sampleGapMilliseconds")] public double SampleGapMilliseconds => Sampler.Probe.CurrentStartToStartGapMilliseconds ?? 0;
    [JsonPropertyName("lastReadMeaning")] public string LastReadMeaning => "Previous completed sampler iteration; excludes the active in-flight iteration";
    [JsonPropertyName("sampleGapMeaning")] public string SampleGapMeaning => "Latest observed interval between iteration starts";
    // Publication and telemetry are separate atomic references. Report the
    // correlation explicitly instead of silently attaching newer probe data.
    [JsonPropertyName("sampleTimeMatchesLastPublication")] public bool SampleTimeMatchesLastPublication => Sample.Time == Sampler.Probe.LastPublishedSampleTime;
    public double? CpuTemperature => Sample.CpuTemperature;
    public double? GpuTemperature => Sample.GpuTemperature;
    public int ProcessId => Environment.ProcessId;
    public string ApplicationVersion => typeof(Engine).Assembly.GetName().Version?.ToString() ?? "";
}

internal sealed class TelemetryDecisionException(ControllerFaultEvidence evidence) : IOException(evidence.Reason)
{
    internal ControllerFaultEvidence Evidence { get; } = evidence;
}
