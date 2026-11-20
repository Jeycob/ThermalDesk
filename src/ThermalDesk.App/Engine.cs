using System.Collections.Concurrent;
using System.Diagnostics;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class Engine
{
    private readonly Func<string, IFanController> openController;
    private readonly Func<Telemetry>? testTelemetry;
    private readonly Func<string[]> conflicts;
    private readonly TimeSpan initialTemperatureTimeout;
    private readonly Func<bool, int, ILcd> openDisplay;
    private readonly TimeSpan displayRetryDelay;
    private readonly TimeSpan stopWaitTimeout;
    public Engine() : this(serial => new FanController(serial), null, Conflicts) { }
    internal Engine(Func<string, IFanController> controllerFactory, Func<Telemetry>? telemetryReader, Func<string[]> findConflicts, TimeSpan? startupTimeout = null, Func<bool, int, ILcd>? displayFactory = null, TimeSpan? lcdRetryDelay = null, TimeSpan? stopWaitTimeout = null)
    {
        openController = controllerFactory; testTelemetry = telemetryReader; conflicts = findConflicts;
        initialTemperatureTimeout = startupTimeout ?? TimeSpan.FromSeconds(20);
        if (initialTemperatureTimeout <= TimeSpan.Zero || initialTemperatureTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        openDisplay = displayFactory ?? ((panel, brightness) => panel ? new PanelLcd(brightness) : new AioLcd(brightness));
        displayRetryDelay = lcdRetryDelay ?? TimeSpan.FromSeconds(5);
        if (displayRetryDelay < TimeSpan.FromMilliseconds(20) || displayRetryDelay > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(lcdRetryDelay));
        this.stopWaitTimeout = stopWaitTimeout ?? TimeSpan.FromSeconds(12);
        if (this.stopWaitTimeout <= TimeSpan.Zero || this.stopWaitTimeout > TimeSpan.FromSeconds(12)) throw new ArgumentOutOfRangeException(nameof(stopWaitTimeout));
    }
    private readonly object lifecycleGate = new();
    private long lifecycleVersion;
    private CancellationTokenSource? stop;
    private Task[] tasks = [];
    private Task? stopCompletion;
    private volatile bool powerPaused;
    internal bool PowerPaused => powerPaused;
    private readonly ConcurrentDictionary<string, string> cleanupFailures = new();
    internal bool CleanupFailed => !cleanupFailures.IsEmpty;
    private string cleanupStatus = "No cleanup requested";
    internal string CleanupStatus => Volatile.Read(ref cleanupStatus);
    private Telemetry latest = new(DateTimeOffset.MinValue, null, null, null, null, null);
    private ProcessActivity activity = ProcessActivity.Disabled;
    private IReadOnlyDictionary<string, LogExcerpt> excerpts = LogExcerpt.Empty;
    private sealed record Merged(Telemetry Sensor, ProcessActivity Activity, IReadOnlyDictionary<string, LogExcerpt> Logs, Telemetry Value);
    private Merged? merged;
    private readonly ConcurrentQueue<string> processLog = new();
    public string[] ProcessLogs => processLog.ToArray();
    private readonly ConcurrentQueue<string> log = new();
    private volatile bool conflictingController;
    private Settings current = new();
    internal OnlineMediaCache OnlineMedia { get; } = new();
    private volatile bool controllerFault;
    public bool ControllerFault => controllerFault;
    private readonly SamplerDiagnosticsRecorder samplerDiagnostics = new();
    internal SamplerDiagnosticSnapshot SamplerDiagnostics => samplerDiagnostics.Snapshot();
    private ControllerFaultEvidence? lastControllerFault;
    internal ControllerFaultEvidence? LastControllerFault => Volatile.Read(ref lastControllerFault);
    private TelemetryDecisionException TelemetryFault(string reason, Telemetry sample, DateTimeOffset decisionTime) =>
        new(new(decisionTime, reason, sample, SamplerDiagnostics, true));
    public Telemetry Latest
    {
        get
        {
            var t = Volatile.Read(ref latest); var a = Volatile.Read(ref activity);
            var logs = Volatile.Read(ref excerpts); var cached = Volatile.Read(ref merged);
            if (cached is not null && ReferenceEquals(cached.Sensor, t) && ReferenceEquals(cached.Activity, a) && ReferenceEquals(cached.Logs, logs)) return cached.Value;
            var value = t with { TopCpuProcess = a.Cpu, TopGpuProcess = a.Gpu, ProcessSampleTime = a.Time, ProcessMonitorStatus = a.Status, LogExcerpts = logs };
            Volatile.Write(ref merged, new(t, a, logs, value)); return value;
        }
    }
    public ConcurrentDictionary<string, string> Status { get; } = new();
    public ConcurrentDictionary<int, FanReading> Fans { get; } = new();
    public ConcurrentDictionary<int, FanControlState> FanCommands { get; } = new();
    internal ConcurrentDictionary<string, int> FrameRenderCounts { get; } = new();
    internal static int SensorInterval(Settings settings) => settings.CoolingEnabled ? Math.Min(settings.PollSeconds, 2) : settings.PollSeconds;
    public string[] Logs => log.ToArray();
    public bool Running { get { lock (lifecycleGate) return stop is not null || stopCompletion is { IsCompleted: false }; } }
    public bool Cancelled => stop?.IsCancellationRequested == true;
    public bool Live { get; private set; }
    public static string[] Conflicts()
    {
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
                try
                {
                    if (new[] { "TTRGBPlus", "OpenRGB", "FanControl", "SignalRgb", "SignalRGB" }.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) names.Add(p.ProcessName);
                }
                catch (InvalidOperationException) { }
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public void Start(Settings settings, bool live)
    {
        long requestedVersion;
        lock (lifecycleGate)
        {
            if (Running) throw new InvalidOperationException("Stop the current session first.");
            requestedVersion = lifecycleVersion;
        }
        settings.Validate();
        if (live) settings.ValidateLiveSelection();
        if (live && settings.CoolingEnabled && testTelemetry is null && !StartupManager.Elevated)
            throw new InvalidOperationException("CPU sensor access on this PC requires administrator rights. Restart as administrator before enabling cooling.");
        if (live && settings.HasUsbSelection && conflicts().Length != 0) throw new InvalidOperationException("Close conflicting hardware-control applications: " + string.Join(", ", conflicts()));
        var prepared = settings.Copy();
        lock (lifecycleGate)
        {
            if (Running) throw new InvalidOperationException("Stop the current session first; its previous workers must finish before another session starts.");
            if (requestedVersion != lifecycleVersion) throw new InvalidOperationException("Start was interrupted by a stop or Windows suspend request. Start explicitly again after the transition completes.");
            StartSession(prepared, live);
        }
    }
    private void StartSession(Settings s, bool live)
    {
        stopCompletion = null; powerPaused = false; cleanupFailures.Clear(); Volatile.Write(ref cleanupStatus, "No cleanup requested");
        Volatile.Write(ref current, s); stop = new(); var token = stop.Token; Live = live; conflictingController = false; controllerFault = false;
        Status.Clear(); Fans.Clear(); FanCommands.Clear(); FrameRenderCounts.Clear();
        samplerDiagnostics.Reset(); Volatile.Write(ref lastControllerFault, null);
        latest = new(DateTimeOffset.MinValue, null, null, null, null, null);
        activity = ProcessActivity.Disabled; processLog.Clear();
        Volatile.Write(ref excerpts, LogExcerpt.Empty); Volatile.Write(ref merged, null);
        Status["Session"] = live ? "LIVE · configured devices only" : "PREVIEW · no hardware access";
        Status["TOUGHLIQUID AX"] = s.Aio.Enabled ? "Enabled · waiting for connection" : "Disabled · enable in Displays";
        Status["480×128 panel"] = s.Panel.Enabled ? "Enabled · waiting for connection" : "Disabled · enable in Displays";
        Note(live ? "Live session started. No flash writes or firmware updates." : "Preview started. All values are simulated.");
        if (!live && s.StartLiveAtSignIn && !s.HasLiveSelection)
        {
            Status["Session"] = "PREVIEW · all live devices disabled · sign-in preference retained";
            Note("Live sign-in preference retained, but no live devices are selected. Opened in Preview without hardware access; enable a device before starting live control.");
        }
        // Hardware reads can block inside a driver. Keep the safety sampler off
        // the shared pool used by LCD JPEGs, online images and process queries.
        var workers = new List<Task> { Task.Factory.StartNew(() => SensorLoop(live, token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default) };
        if (live)
        {
            // Disabled workers wait without opening hardware. Each LCD can be enabled,
            // disabled or reconfigured without releasing the fan controller.
            workers.Add(Task.Run(() => LcdLoop("480×128 panel", true, token)));
            workers.Add(Task.Run(() => LcdLoop("TOUGHLIQUID AX", false, token)));
            workers.Add(Task.Run(() => FanLoop(s, token)));
            workers.Add(Task.Run(() => OnlineMedia.Run(() => Volatile.Read(ref current), token)));
            // Process enumeration/PDH must never run on the cooling sensor loop.
            // Mock hardware checks never enumerate the host's real processes.
            if (testTelemetry is null) { workers.Add(Task.Run(() => ProcessLoop(token))); workers.Add(Task.Run(() => LogLoop(token))); workers.Add(Task.Run(() => DailyEnergyLoop(token))); }
        }
        tasks = workers.ToArray();
    }
    public void ValidateLiveSettings(Settings settings)
    {
        if (!Running || !Live || Cancelled || ControllerFault) throw new InvalidOperationException("No healthy live session. Stop and resolve the fault before reconnecting.");
        // An existing session may become idle when its last RGB/LCD output is
        // disabled. Keep the shared controller open, clear selected LEDs once,
        // and never reconnect it merely to apply an OFF request. Only Start
        // requires at least one selected live device.
        settings.Validate();
        var before = Volatile.Read(ref current); var next = settings.Copy();
        if (!before.HasUsbSelection && next.HasUsbSelection && conflicts().Length > 0)
            throw new InvalidOperationException("Close conflicting controllers before enabling a USB output. Monitoring-only can remain active.");
        bool SamePorts() => before.Ports.All(p =>
        {
            var other = next.Ports.Single(x => x.Port == p.Port);
            return p.Role == other.Role && (p.Mode == ControlMode.Preserve) == (other.Mode == ControlMode.Preserve);
        });
        if (before.ControllerSerial != next.ControllerSerial || before.CoolingEnabled != next.CoolingEnabled || !SamePorts())
            throw new InvalidOperationException("Changing the fan controller, cooling ownership or port wiring requires Stop first. Curves, RGB, sensor polling and LCD edits apply automatically without restarting fans.");
    }
    public void ApplyLiveSettings(Settings settings)
    {
        ValidateLiveSettings(settings);
        var next = settings.Copy();
        Volatile.Write(ref current, next);
        Note("Live settings applied in place. Controller was not reinitialized; fan safety history retained.");
    }
    public async Task Stop()
    {
        var completion = BeginStop(false);
        // The deadline belongs to this caller, never to the ownership epoch.
        // A timed-out waiter cannot discard still-running USB/sensor workers.
        if (await Task.WhenAny(completion, Task.Delay(stopWaitTimeout)).ConfigureAwait(false) != completion)
            throw new TimeoutException("A hardware operation is still pending. Session is stopping; do not start another controller.");
        await completion.ConfigureAwait(false);
    }
    internal Task PauseForSystemSleep() => BeginStop(true);
    internal void NotifySystemResume()
    {
        if (PowerPaused) Note(Running ? "Windows resumed; waiting for the previous session to release its hardware. Live remains paused."
            : "Windows resumed; Live remains paused until explicitly started. " + CleanupStatus);
    }
    private Task BeginStop(bool forSystemSleep)
    {
        lock (lifecycleGate)
        {
            // Also invalidates a Start still validating outside this short lock.
            lifecycleVersion++;
            if (forSystemSleep && !powerPaused)
            {
                powerPaused = true;
                Note("Windows suspend requested; pausing the session. No automatic Live restart after resume.");
            }
            if (stopCompletion is not null) return stopCompletion;
            if (stop is null)
            {
                if (powerPaused) Status["Session"] = "Paused for Windows sleep · explicit Live start required";
                return Task.CompletedTask;
            }
            var source = stop; var workers = tasks;
            // CancelAsync flags the token before returning; callbacks cannot
            // block the Windows power-notification handler.
            var callbacks = source.CancelAsync();
            Volatile.Write(ref cleanupStatus, "Pending · waiting for previous workers and hardware cleanup");
            Status["Session"] = powerPaused ? "Pausing for Windows sleep · hardware release pending" : "Stopping · hardware release pending";
            // No UI synchronization context is captured, including the prefix.
            // This SAME task is shared by manual Stop, suspend and exit.
            stopCompletion = Task.Run(() => CompleteStop(source, workers, callbacks));
            return stopCompletion;
        }
    }
    private async Task CompleteStop(CancellationTokenSource source, Task[] workers, Task callbacks)
    {
        try { await callbacks.ConfigureAwait(false); }
        catch (Exception error) { RecordCleanupFailure("Cancellation callbacks", error); }
        var all = Task.WhenAll(workers);
        try { await all.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!all.IsFaulted) { }
        catch (Exception error)
        {
            RecordCleanupFailure("Worker completion", error);
            Note("Worker exited with error during stop: " + error.Message);
        }
        lock (lifecycleGate)
        {
            // Start cannot enter a new epoch while this completion is pending.
            source.Dispose(); tasks = []; Live = false;
            FanCommands.Clear(); Fans.Clear();
            activity = ProcessActivity.Disabled; processLog.Clear();
            Volatile.Write(ref excerpts, LogExcerpt.Empty); Volatile.Write(ref merged, null);
            Volatile.Write(ref latest, Latest with { Fans = [] });
            if (!controllerFault && !CleanupFailed)
            {
                foreach (var key in Status.Keys.Where(k => k.StartsWith("Port ", StringComparison.Ordinal))) Status[key] = "Released · no live command / feedback";
                if (Status.ContainsKey("Controller")) Status["Controller"] = "Released · no live controller feedback";
            }
            string result = CleanupFailed ? "Failed · " + string.Join("; ", cleanupFailures.OrderBy(x => x.Key).Select(x => x.Key + ": " + x.Value))
                : "Completed · prior workers ended; release requests and close calls completed; hardware duty is not independently confirmed";
            Volatile.Write(ref cleanupStatus, result); SetStatus("Cleanup", result);
            Status["Session"] = powerPaused ? "Paused for Windows sleep · explicit Live start required" : "Stopped";
            if (CleanupFailed) Status["Session"] += " · cleanup failed; verify hardware control";
            Note((powerPaused ? "Paused for Windows sleep. " : "Stopped. ") + result + ". Restoration is not BIOS hand-back or a guaranteed hardware watchdog.");
            stop = null;
        }
    }
    private void RecordCleanupFailure(string owner, Exception error)
    {
        cleanupFailures.TryAdd(owner, error.Message);
        Volatile.Write(ref cleanupStatus, "Failed · " + owner + ": " + error.Message);
        SetStatus(owner + " cleanup", "ERROR · " + error.Message);
    }
    private void SensorLoop(bool live, CancellationToken token)
    {
        Monitoring? monitor = null;
        var history = new TelemetryHistory(live && testTelemetry is null ? DailyEnergyStore.Load() : null,
            restoredRollingEnergy: live && testTelemetry is null ? RollingEnergyStore.Load() : null);
        try
        {
            while (!token.IsCancellationRequested)
            {
                int seconds = SensorInterval(Volatile.Read(ref current));
                long sampleStart = Stopwatch.GetTimestamp();
                samplerDiagnostics.BeginIteration(DateTimeOffset.UtcNow, sampleStart);
                try
                {
                    if (live)
                    {
                        samplerDiagnostics.EnterPhase("ConflictScan");
                        conflictingController = Volatile.Read(ref current).HasUsbSelection && conflicts().Length > 0;
                        if (conflictingController) { stop?.Cancel(); Note("Conflicting controller detected; cancelling live control."); }
                        Telemetry reading;
                        if (testTelemetry is not null)
                        {
                            samplerDiagnostics.BeginMeasurement(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
                            reading = testTelemetry();
                        }
                        else
                        {
                            if (monitor is null) { samplerDiagnostics.EnterPhase("MonitorInit"); monitor = new(); }
                            reading = monitor.Read(Volatile.Read(ref current).PreferredGpuId, samplerDiagnostics);
                        }
                        samplerDiagnostics.EnterPhase("History");
                        var published = history.Add(reading with { Fans = Fans.Values.OrderBy(f => f.Port).ToArray(), ExpectedSampleSeconds = seconds }, Volatile.Read(ref current));
                        Volatile.Write(ref latest, published); samplerDiagnostics.Published(published);
                    }
                    else
                    {
                        samplerDiagnostics.BeginMeasurement(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
                        double x = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 20d;
                        var preview = new Telemetry(DateTimeOffset.UtcNow, 48 + Math.Sin(x) * 5, 42 + Math.Cos(x) * 4, 12, 8, 31, "Simulated preview")
                        { CpuPowerWatts = 50 + Math.Sin(x) * 10, GpuPowerWatts = 35 + Math.Cos(x) * 12, ExpectedSampleSeconds = seconds };
                        samplerDiagnostics.EnterPhase("History");
                        var published = history.Add(preview, Volatile.Read(ref current));
                        Volatile.Write(ref latest, published); samplerDiagnostics.Published(published);
                    }
                    SetStatus("Sensors", (Latest.Warning.Length == 0 ? "CPU / GPU / RAM · shared sampler" : Latest.Warning) + $" · {seconds} s interval");
                }
                catch (Exception e)
                {
                    var unavailable = new Telemetry(DateTimeOffset.UtcNow, null, null, null, null, null, e.Message);
                    Volatile.Write(ref latest, unavailable); samplerDiagnostics.Published(unavailable);
                    SetStatus("Sensors", "ERROR · " + e.Message);
                    samplerDiagnostics.EnterPhase("MonitorDispose"); monitor?.Dispose(); monitor = null;
                }
                samplerDiagnostics.CompleteIteration();
                // PollSeconds is a start-to-start interval. Adding a full delay
                // after a slow read could age a valid sample past the unchanged
                // five-second cooling limit before the next read completed.
                var remaining = TimeSpan.FromSeconds(seconds) - Stopwatch.GetElapsedTime(sampleStart);
                // Avoid a tight retry loop when a driver consistently overruns.
                token.WaitHandle.WaitOne(remaining > TimeSpan.FromMilliseconds(20) ? remaining : TimeSpan.FromMilliseconds(20));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            samplerDiagnostics.EnterPhase("MonitorDispose");
            try { monitor?.Dispose(); }
            finally { samplerDiagnostics.Stopped(); }
        }
    }
    private async Task DailyEnergyLoop(CancellationToken token)
    {
        void Save()
        {
            try { var latest = Latest; DailyEnergyStore.Save(latest.EnergyToday); RollingEnergyStore.Save(latest.Energy24h); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetStatus("Daily energy", "Persistence unavailable · " + e.GetType().Name); }
        }
        try { while (!token.IsCancellationRequested) { await Task.Delay(TimeSpan.FromMinutes(1), token); Save(); } }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Save(); }
    }
    private async Task ProcessLoop(CancellationToken token)
    {
        ProcessActivityMonitor? monitor = null; long sampled = 0; string lastKey = "";
        try
        {
            while (!token.IsCancellationRequested)
            {
                var s = Volatile.Read(ref current);
                if (!s.ProcessMonitorEnabled && !LogWidget.NeedsProcesses(s))
                {
                    monitor?.Dispose(); monitor = null; sampled = 0; lastKey = "";
                    Volatile.Write(ref activity, ProcessActivity.Disabled); processLog.Clear();
                }
                else if (sampled == 0 || Stopwatch.GetElapsedTime(sampled).TotalSeconds >= s.ProcessSampleSeconds)
                {
                    sampled = Stopwatch.GetTimestamp();
                    try
                    {
                        monitor ??= new(); var next = monitor.Read(); Volatile.Write(ref activity, next);
                        string key = $"{next.Cpu?.Pid}:{Math.Round(next.Cpu?.Percent ?? 0)}|{next.Gpu?.Pid}:{Math.Round(next.Gpu?.Percent ?? 0)}|{next.Status}";
                        if (key != lastKey)
                        {
                            processLog.Enqueue($"{next.Time.LocalDateTime:HH:mm:ss} CPU {next.Cpu?.Summary ?? "—"} | GPU {next.Gpu?.Summary ?? "—"}");
                            while (processLog.Count > 20) processLog.TryDequeue(out _);
                            lastKey = key;
                        }
                    }
                    catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException)
                    { Volatile.Write(ref activity, new ProcessActivity(DateTimeOffset.UtcNow, null, null, "Process sample unavailable · " + e.GetType().Name)); }
                }
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { monitor?.Dispose(); Volatile.Write(ref activity, ProcessActivity.Disabled); }
    }
    private async Task LogLoop(CancellationToken token)
    {
        ProcessLogSampler? sampler = null; long sampled = 0; Settings? previous = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var settings = Volatile.Read(ref current);
                if (!LogWidget.Selected(settings).Any())
                { sampler = null; sampled = 0; Volatile.Write(ref excerpts, LogExcerpt.Empty); }
                else if (!ReferenceEquals(previous, settings) || sampled == 0 || Stopwatch.GetElapsedTime(sampled).TotalSeconds >= settings.LogSampleSeconds)
                {
                    sampler ??= new(); sampled = Stopwatch.GetTimestamp();
                    try
                    {
                        var value = sampler.Read(settings, Latest); Volatile.Write(ref excerpts, value);
                        SetStatus("Log widgets", string.Join(" · ", value.Values.Select(v => v.Path.Length == 0 ? v.Status : v.Status + " · " + v.Path)));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException)
                    { Volatile.Write(ref excerpts, LogExcerpt.Empty); SetStatus("Log widgets", "Unavailable · " + e.GetType().Name); }
                }
                previous = settings;
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Volatile.Write(ref excerpts, LogExcerpt.Empty); }
    }
    private void Guard(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (conflictingController)
        {
            stop?.Cancel();
            throw new IOException("Another hardware controller started. Session cancelled to avoid conflicting writes.");
        }
    }
    private async Task LcdLoop(string name, bool panel, CancellationToken token)
    {
        using var renderer = new FrameRenderer();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var requested = Volatile.Read(ref current);
                var settings = panel ? requested.Panel : requested.Aio;
                if (!settings.Enabled)
                {
                    SetStatus(name, "Disabled · layout remains editable offline");
                    await Task.Delay(1000, token);
                    continue;
                }
                ILcd? lcd = null;
                bool releaseFailed = false;
                bool reconfigured = false;
                try
                {
                    Guard(token); lcd = openDisplay(panel, settings.Brightness);
                    SetStatus(name, "Connected · " + settings.Content);
                    byte[]? encoded = null; string? previousKey = null; DisplaySettings? previousDisplay = null;
                    while (!token.IsCancellationRequested)
                    {
                        Guard(token);
                        var active = Volatile.Read(ref current); var display = panel ? active.Panel : active.Aio;
                        if (!display.Enabled || display.Brightness != settings.Brightness)
                        {
                            reconfigured = true;
                            break;
                        }
                        var telemetry = Latest with { RenderTime = DateTimeOffset.Now };
                        string? key = FrameRenderer.VisualKey(telemetry, display, OnlineMedia);
                        if (encoded is null || key is null || key != previousKey || !ReferenceEquals(previousDisplay, display))
                        {
                            using var image = renderer.Render(telemetry, display, 480, panel ? 128 : 480, OnlineMedia);
                            encoded = renderer.Encode(image); previousKey = key; previousDisplay = display;
                            FrameRenderCounts.AddOrUpdate(name, 1, (_, count) => count + 1);
                        }
                        lcd.Send(encoded); lcd.Heartbeat();
                        // Heartbeats stay at <= 2 s even when sensor polling is slower.
                        await Task.Delay(TimeSpan.FromSeconds(1d / display.FramesPerSecond), token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception e) { SetStatus(name, "ERROR · " + e.Message); }
                finally
                {
                    try
                    {
                        if (lcd is not null)
                        {
                            SetStatus(name + " cleanup", "PENDING · closing the previous LCD transport");
                            lcd.Dispose();
                            SetStatus(name + " cleanup", "Completed · LCD transport close returned");
                            if (token.IsCancellationRequested && Status.TryGetValue(name, out var lastStatus) && lastStatus.StartsWith("Connected", StringComparison.Ordinal))
                                SetStatus(name, "Released · no live LCD transport");
                        }
                    }
                    catch (Exception error)
                    {
                        releaseFailed = true;
                        RecordCleanupFailure(name, error);
                        SetStatus(name, "ERROR · close failed; retry blocked until Stop/review · " + error.Message);
                    }
                }
                if (releaseFailed) return;
                if (reconfigured) continue;
                await Task.Delay(displayRetryDelay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task FanLoop(Settings s, CancellationToken token)
    {
        var policies = s.Ports.ToDictionary(p => p.Port, _ => new FanSafety());
        var monitored = s.Ports.Where(p => s.CoolingEnabled && p.Mode != ControlMode.Preserve).ToArray();
        try
        {
            while (!token.IsCancellationRequested)
            {
                IFanController? controller = null;
                bool controllerAttempted = false;
                string? controllerFailureStatus = null;
                string? controllerFailureReason = null;
                try
                {
                    while (true)
                    {
                        Guard(token); s = Volatile.Read(ref current);
                        if (s.CoolingEnabled || s.RgbEnabled && s.Ports.Any(Lighting.IsManaged) || s.MonitorFanController) break;
                        await Task.Delay(250, token);
                    }
                    // Drivers can publish an initial timestamp before temperatures are ready.
                    // Wait BEFORE opening USB. This is not a reconnect or a full-speed fallback.
                    var startupWait = Stopwatch.StartNew();
                    Dictionary<int, FanDecision> initialDecisions;
                    while (true)
                    {
                        Guard(token); s = Volatile.Read(ref current);
                        monitored = s.Ports.Where(p => s.CoolingEnabled && p.Mode != ControlMode.Preserve).ToArray();
                        // No duty was sent yet, so do not inherit smoothing history from
                        // a partial/invalid readiness sample or an older pending profile.
                        foreach (var policy in policies.Values) policy.Reset();
                        var sample = Latest; var now = DateTimeOffset.UtcNow;
                        initialDecisions = monitored.ToDictionary(p => p.Port, p => policies[p.Port].Evaluate(p, sample, now, 0));
                        var notReady = initialDecisions.Values.FirstOrDefault(d => d.Duty is null);
                        if (notReady is null) break;
                        if (startupWait.Elapsed >= initialTemperatureTimeout)
                            throw TelemetryFault("Temperature readiness timed out. No fan controller opened or speed command sent. Keep vendor cooling active. " + notReady.Reason, sample, now);
                        SetStatus("Controller", "Waiting for valid temperatures · no fan-controller commands · " + notReady.Reason);
                        await Task.Delay(50, token);
                    }
                    Guard(token); controllerAttempted = true; controller = openController(s.ControllerSerial);
                    var clock = Stopwatch.StartNew();
                    var confirmations = new Dictionary<int, DutyConfirmation>();
                    void ProtectStartedPorts()
                    {
                        Guard(token);
                        if (!FanSafety.IsCritical(Latest, DateTimeOffset.UtcNow)) return;
                        foreach (var (port, confirmation) in confirmations)
                        {
                            if (confirmation.RequestedDuty == 100) continue;
                            Guard(token);
                            var activePort = Volatile.Read(ref current).Ports.Single(p => p.Port == port);
                            var decisionSample = Latest; var decisionTime = DateTimeOffset.UtcNow;
                            var decision = policies[port].Evaluate(activePort, decisionSample, decisionTime, 0);
                            if (decision.Duty is null) throw TelemetryFault(decision.Reason, decisionSample, decisionTime);
                            controller.SetDuty(port, decision.Duty.Value);
                            confirmation.Command(decision.Duty.Value, clock.Elapsed.TotalSeconds);
                            FanCommands[port] = new(port, decision.Duty.Value, decision.TargetDuty ?? decision.Duty.Value, decision.Reason + " · " + confirmation.Description, DateTimeOffset.UtcNow);
                        }
                    }
                    foreach (var p in monitored)
                    {
                        ProtectStartedPorts(); controller.CaptureBaseline(p.Port); ProtectStartedPorts();
                        // Opening USB and capturing a baseline may block. Neither
                        // the readiness sample nor the old profile authorizes a
                        // later speed write after cancellation or a thermal change.
                        var activePort = Volatile.Read(ref current).Ports.Single(x => x.Port == p.Port);
                        policies[p.Port].Reset();
                        var initialSample = Latest; var initialTime = DateTimeOffset.UtcNow;
                        var initial = policies[p.Port].Evaluate(activePort, initialSample, initialTime, 0);
                        if (initial.Duty is null) throw TelemetryFault(initial.Reason, initialSample, initialTime);
                        controller.SetDuty(p.Port, initial.Duty.Value);
                        confirmations[p.Port] = new(initial.Duty.Value, clock.Elapsed.TotalSeconds);
                        FanCommands[p.Port] = new(p.Port, initial.Duty.Value, initial.TargetDuty ?? initial.Duty.Value, initial.Reason + " · " + confirmations[p.Port].Description, DateTimeOffset.UtcNow);
                    }
                    SetStatus("Controller", "Connected · 264A:232B");
                    var failures = monitored.ToDictionary(p => p.Port, _ => 0);
                    double nextControl = 0, nextFeedback = 0, nextLight = 0, previousControl = clock.Elapsed.TotalSeconds;
                    var lastLighting = new Dictionary<int, LedColor[]>(); double lastKeepalive = -2;
                    Settings? scheduleProfile = null; DateTime scheduleMinute = DateTime.MinValue;
                    var schedule = ScheduledLighting.Unchanged;
                    void ControlIfDue()
                    {
                        Guard(token);
                        s = Volatile.Read(ref current);
                        monitored = s.Ports.Where(p => s.CoolingEnabled && p.Mode != ControlMode.Preserve).ToArray();
                        // A read may cross the safety age limit before the next
                        // scheduled curve evaluation. Never retry an unconfirmed
                        // command from expired data in that intervening window.
                        var freshnessSample = Latest; var freshnessTime = DateTimeOffset.UtcNow;
                        if (monitored.Length > 0 && !FanSafety.IsFresh(freshnessSample, freshnessTime))
                            throw TelemetryFault("Stale telemetry · release software control; do not force full speed", freshnessSample, freshnessTime);
                        double controlTime = clock.Elapsed.TotalSeconds;
                        bool due = controlTime >= nextControl;
                        // A critical sample takes priority between HID calls,
                        // including inside a slow feedback sweep. A native call
                        // can outlast its nominal timeout while cancellation completes.
                        if (!due && !FanSafety.IsCritical(Latest, DateTimeOffset.UtcNow)) return;
                        double controlSeconds = due ? controlTime - previousControl : 0;
                        if (due)
                        {
                            previousControl = controlTime;
                            nextControl = controlTime + Math.Min(s.PollSeconds, 2);
                        }
                        foreach (var p in monitored)
                        {
                            Guard(token);
                            var decisionSample = Latest; var decisionTime = DateTimeOffset.UtcNow;
                            var decision = policies[p.Port].Evaluate(p, decisionSample, decisionTime, controlSeconds);
                            if (decision.Duty is null) throw TelemetryFault(decision.Reason, decisionSample, decisionTime);
                            var confirmation = confirmations[p.Port];
                            if (confirmation.RequestedDuty != decision.Duty.Value)
                            {
                                controller.SetDuty(p.Port, decision.Duty.Value);
                                confirmation.Command(decision.Duty.Value, clock.Elapsed.TotalSeconds);
                            }
                            string reason = decision.Reason + " · " + confirmation.Description;
                            FanCommands[p.Port] = new(p.Port, decision.Duty.Value, decision.TargetDuty ?? decision.Duty.Value, reason, DateTimeOffset.UtcNow);
                            SetStatus($"Port {p.Port}", $"{decision.Duty}% · {reason}");
                        }
                    }
                    while (!token.IsCancellationRequested)
                    {
                        ControlIfDue();
                        // Turning lighting off is explicit user intent. Send black once;
                        // do not close/reinitialize the shared cooling controller.
                        foreach (var disabled in lastLighting.Keys.Where(port => !s.RgbEnabled || !s.Ports.Any(p => p.Port == port && Lighting.IsManaged(p))).ToArray())
                        {
                            ControlIfDue();
                            controller.SetRgbFrame(disabled, new LedColor[lastLighting[disabled].Length]);
                            lastLighting.Remove(disabled);
                            SetStatus($"RGB port {disabled}", "Released · last output cleared; no continuing lighting writes");
                        }
                        // Explicit OFF owns lighting only; it does not imply RPM
                        // monitoring. Dark ports are still included in the
                        // managed lighting frame loop below.
                        foreach (var stalePort in Fans.Keys.Where(port => !s.Ports.Any(p => p.Port == port && (s.MonitorFanController || monitored.Any(m => m.Port == port) || s.RgbEnabled && p.Rgb))).ToArray())
                            Fans.TryRemove(stalePort, out _);
                        if (clock.Elapsed.TotalSeconds >= nextFeedback)
                        {
                            // Both deadlines are start-to-start. Feedback and RGB
                            // never add their total transfer time to control delay.
                            nextFeedback = clock.Elapsed.TotalSeconds + Math.Min(s.PollSeconds, 2);
                            foreach (var p in s.Ports.Where(p => s.MonitorFanController || monitored.Any(m => m.Port == p.Port) || s.RgbEnabled && p.Rgb).ToArray())
                            {
                                ControlIfDue();
                                int? requestedAtRead = confirmations.TryGetValue(p.Port, out var confirmation) ? confirmation.RequestedDuty : null;
                                var reading = controller.Read(p.Port); Fans[p.Port] = reading;
                                ControlIfDue();
                                if (failures.ContainsKey(p.Port))
                                {
                                    failures[p.Port] = reading.Rpm == 0 ? failures[p.Port] + 1 : 0;
                                    if (failures[p.Port] >= 3) throw new IOException($"Port {p.Port}: repeated zero RPM. Check wiring; releasing captured duty, not forcing full speed.");
                                    // Compare against the request that existed during
                                    // the read, even if an urgent command just changed it.
                                    if (confirmation!.Observe(p.Port, reading.Duty, requestedAtRead!.Value, clock.Elapsed.TotalSeconds))
                                    {
                                        Guard(token); controller.SetDuty(p.Port, confirmation.RequestedDuty);
                                        Note($"Port {p.Port}: duty readback {reading.Duty}% differs from requested {confirmation.RequestedDuty}%; retry {confirmation.Retries}/{DutyConfirmation.MaximumRetries} after {DutyConfirmation.GraceSeconds:0.0} s confirmation grace.");
                                    }
                                    SetStatus($"Port {p.Port}", $"{confirmation.RequestedDuty}% · {confirmation.Description}");
                                }
                            }
                        }
                        if (s.RgbEnabled && clock.Elapsed.TotalSeconds >= nextLight)
                        {
                            double elapsed = clock.Elapsed.TotalSeconds; bool keepalive = elapsed - lastKeepalive >= 2;
                            var local = DateTime.Now; var minute = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0);
                            if (!ReferenceEquals(scheduleProfile, s) || minute != scheduleMinute)
                            { schedule = RgbSchedule.At(s, local); scheduleProfile = s; scheduleMinute = minute; }
                            foreach (var p in s.Ports.Where(Lighting.IsManaged))
                            {
                                ControlIfDue();
                                var colors = Lighting.OutputFrame(p, s.RgbColor, elapsed, Latest, schedule);
                                if (keepalive || !lastLighting.TryGetValue(p.Port, out var previous) || !previous.SequenceEqual(colors))
                                { controller.SetRgbFrame(p.Port, colors); lastLighting[p.Port] = colors; }
                                SetStatus($"RGB port {p.Port}", p.Rgb ? $"{schedule.Description} · {p.RgbEffect} · {p.LedCount} LEDs (commanded)" : "OFF · black output (commanded)");
                            }
                            if (keepalive) lastKeepalive = elapsed; nextLight = elapsed + .2;
                        }
                        ControlIfDue();
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((nextControl - clock.Elapsed.TotalSeconds) * 1000, 20, 200)), token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    var fault = ControllerFaultEvidence.FromException(e, () => Latest, () => SamplerDiagnostics);
                    Volatile.Write(ref lastControllerFault, fault);
                    controllerFault = true;
                    controllerFailureReason = e.Message;
                    string cause = e.Message + $" · sample age {fault.SampleAgeSeconds:0.00} s; last read (previous completed iteration) {fault.LastReadMilliseconds:0} ms; sample gap (iteration starts) {fault.SampleGapMilliseconds:0} ms; active phase {fault.Sampler.Phase} {fault.Sampler.ActiveProvider} {fault.Sampler.ActiveDevice} {fault.Sampler.CurrentPhaseElapsedMilliseconds:0} ms";
                    if (testTelemetry is null)
                    {
                        try
                        {
                            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermalDesk", "last-controller-fault.json");
                            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(fault, Settings.JsonOptions));
                        }
                        catch (Exception diagnosticError) when (diagnosticError is IOException or UnauthorizedAccessException) { Note("Could not save controller fault: " + diagnosticError.GetType().Name); }
                    }
                    controllerFailureStatus = controller is null
                        ? (controllerAttempted ? "FAULT LATCHED · connection attempt failed; ownership not established · " : "FAULT LATCHED · startup blocked before fan USB access · ") + cause
                        : "FAULT LATCHED · " + cause;
                    SetStatus("Controller", controllerFailureStatus + (controller is null ? "" : " · release pending; no forced full-speed fallback or automatic reconnect. Restore vendor control."));
                    foreach (var p in monitored) SetStatus($"Port {p.Port}", controller is null
                        ? "NOT STARTED · no software speed ownership established" : "PAUSED · controller fault: " + e.Message + " · release pending; verify cooling");
                    Fans.Clear(); FanCommands.Clear();
                    break;
                }
                finally
                {
                    if (controller is not null)
                    {
                        var t = Latest;
                        bool critical = FanSafety.IsCritical(t, DateTimeOffset.UtcNow);
                        SetStatus("Controller cleanup", "PENDING · requesting " + (critical ? "critical-temperature duty" : "captured duties") + " and closing the controller");
                        Exception? prepareFailure = null, closeFailure = null;
                        try { controller.PrepareToClose(critical); }
                        catch (Exception releaseError)
                        {
                            prepareFailure = releaseError; RecordCleanupFailure("Controller preparation", releaseError);
                        }
                        // Preparation failures must not skip the only close attempt.
                        try { controller.Dispose(); }
                        catch (Exception releaseError)
                        {
                            closeFailure = releaseError; RecordCleanupFailure("Controller release", releaseError);
                        }
                        if (prepareFailure is not null || closeFailure is not null)
                        {
                            var failure = new IOException("Controller cleanup failed: " + string.Join("; ", new[] { prepareFailure?.Message, closeFailure?.Message }.Where(x => x is not null)), closeFailure ?? prepareFailure);
                            // Keep the triggering fault, if any, alongside cleanup evidence.
                            if (LastControllerFault is null) Volatile.Write(ref lastControllerFault, ControllerFaultEvidence.FromException(failure, () => Latest, () => SamplerDiagnostics));
                            controllerFault = true;
                            SetStatus("Controller cleanup", "ERROR · " + failure.Message);
                            SetStatus("Controller", (controllerFailureStatus ?? "FAULT") + " · " + failure.Message + "; release unconfirmed; verify vendor control");
                            foreach (var p in monitored) SetStatus($"Port {p.Port}", "PAUSED · controller fault: " + LastControllerFault!.Reason + " · controller cleanup failed; duty restoration unconfirmed");
                            throw failure;
                        }
                        string released = critical ? "Critical-temperature duty requested; controller close returned"
                            : "Captured duty restoration requested where owned; controller close returned; duty not independently confirmed";
                        SetStatus("Controller cleanup", "Completed · " + released);
                        if (controllerFailureStatus is not null)
                        {
                            SetStatus("Controller", controllerFailureStatus + " · " + released + "; no automatic reconnect. Verify vendor control.");
                            foreach (var p in monitored) SetStatus($"Port {p.Port}", "PAUSED · controller fault: " + (LastControllerFault?.Reason ?? controllerFailureReason) + " · release/close requests completed; no live feedback");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    // One instance per port, owned solely by FanLoop. Changing a moving curve
    // target must not restart the grace window for an unconfirmed controller.
    internal sealed class DutyConfirmation(int requestedDuty, double now)
    {
        internal const double GraceSeconds = 1.5;
        internal const int MaximumRetries = 2;
        private const int Tolerance = 2;
        private double? pendingSince = now;
        private double lastWrite = now, lastRetry;
        internal int RequestedDuty { get; private set; } = requestedDuty;
        internal int? ConfirmedDuty { get; private set; }
        internal int Retries { get; private set; }
        internal string Description => pendingSince.HasValue
            ? $"awaiting duty confirmation · {GraceSeconds:0.0} s grace · retry {Retries}/{MaximumRetries}"
            : $"confirmed duty {ConfirmedDuty}%";
        internal void Command(int duty, double time)
        {
            RequestedDuty = duty; lastWrite = time; pendingSince ??= time;
        }
        internal bool Observe(int port, int actual, int requestedAtRead, double time)
        {
            if (Math.Abs(actual - requestedAtRead) <= Tolerance)
            {
                ConfirmedDuty = actual; Retries = 0;
                pendingSince = Math.Abs(actual - RequestedDuty) <= Tolerance ? null : lastWrite;
                return false;
            }
            pendingSince ??= time;
            if (time - pendingSince.Value < GraceSeconds || Retries > 0 && time - lastRetry < GraceSeconds) return false;
            if (Retries >= MaximumRetries)
                throw new IOException($"Port {port}: duty not confirmed after {MaximumRetries} retries ({GraceSeconds:0.0} s grace); requested {RequestedDuty}%, read {actual}%, last confirmed {ConfirmedDuty?.ToString() ?? "unavailable"}%. Check the controller; releasing captured duty.");
            Retries++; lastRetry = time; return true;
        }
    }
    private void SetStatus(string key, string value)
    {
        if (!Status.TryGetValue(key, out var old) || old != value)
        {
            Status[key] = value;
            // Log state transitions/errors, not continuously changing temperatures/duty.
            if (value.StartsWith("ERROR") || value.StartsWith("FAULT") || key is "Controller" or "Cleanup" || key.EndsWith(" cleanup", StringComparison.Ordinal) || key.Contains("panel") || key.Contains("AX")) Note(key + ": " + value);
        }
    }
    private void Note(string message)
    {
        log.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (log.Count > 250) log.TryDequeue(out _);
    }
}
