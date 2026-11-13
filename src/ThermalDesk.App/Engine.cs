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
    public Engine() : this(serial => new FanController(serial), null, Conflicts) { }
    internal Engine(Func<string, IFanController> controllerFactory, Func<Telemetry>? telemetryReader, Func<string[]> findConflicts, TimeSpan? startupTimeout = null, Func<bool, int, ILcd>? displayFactory = null, TimeSpan? lcdRetryDelay = null)
    {
        openController = controllerFactory; testTelemetry = telemetryReader; conflicts = findConflicts;
        initialTemperatureTimeout = startupTimeout ?? TimeSpan.FromSeconds(20);
        if (initialTemperatureTimeout <= TimeSpan.Zero || initialTemperatureTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        openDisplay = displayFactory ?? ((panel, brightness) => panel ? new PanelLcd(brightness) : new AioLcd(brightness));
        displayRetryDelay = lcdRetryDelay ?? TimeSpan.FromSeconds(5);
        if (displayRetryDelay < TimeSpan.FromMilliseconds(20) || displayRetryDelay > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(lcdRetryDelay));
    }
    private CancellationTokenSource? stop;
    private Task[] tasks = [];
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
    public bool Running => stop is not null;
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
        if (Running) throw new InvalidOperationException("Stop the current session first.");
        settings.Validate();
        if (live) settings.ValidateLiveSelection();
        if (live && settings.CoolingEnabled && testTelemetry is null && !StartupManager.Elevated)
            throw new InvalidOperationException("CPU sensor access on this PC requires administrator rights. Restart as administrator before enabling cooling.");
        if (live && settings.HasUsbSelection && conflicts().Length != 0) throw new InvalidOperationException("Close conflicting hardware-control applications: " + string.Join(", ", conflicts()));
        var s = settings.Copy(); Volatile.Write(ref current, s); stop = new(); var token = stop.Token; Live = live; conflictingController = false; controllerFault = false;
        Status.Clear(); Fans.Clear(); FanCommands.Clear(); FrameRenderCounts.Clear();
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
        var workers = new List<Task> { Task.Run(() => SensorLoop(live, token)) };
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
        if (stop is null) return;
        stop.Cancel();
        var all = Task.WhenAll(tasks);
        if (await Task.WhenAny(all, Task.Delay(12000)) != all)
            throw new TimeoutException("A hardware operation is still pending. Session is stopping; do not start another controller.");
        try { await all; }
        catch (OperationCanceledException) { }
        catch (Exception e) { Note("Worker exited with error during stop: " + e.Message); }
        stop.Dispose(); stop = null; tasks = []; Live = false;
        FanCommands.Clear(); Fans.Clear();
        activity = ProcessActivity.Disabled; processLog.Clear();
        Volatile.Write(ref excerpts, LogExcerpt.Empty); Volatile.Write(ref merged, null);
        Volatile.Write(ref latest, Latest with { Fans = [] });
        foreach (var key in Status.Keys.Where(k => k.StartsWith("Port ", StringComparison.Ordinal))) Status[key] = "Released · no live command / feedback";
        if (!controllerFault && Status.ContainsKey("Controller")) Status["Controller"] = "Released · no live controller feedback";
        Status["Session"] = "Stopped"; Note("Stopped. Captured fan duties requested back, except a real critical-temperature override. Restoration is not BIOS hand-back or a guaranteed hardware watchdog.");
    }
    private async Task SensorLoop(bool live, CancellationToken token)
    {
        Monitoring? monitor = null;
        var history = new TelemetryHistory(live && testTelemetry is null ? DailyEnergyStore.Load() : null,
            restoredRollingEnergy: live && testTelemetry is null ? RollingEnergyStore.Load() : null);
        try
        {
            while (!token.IsCancellationRequested)
            {
                int seconds = SensorInterval(Volatile.Read(ref current));
                try
                {
                    if (live)
                    {
                        conflictingController = Volatile.Read(ref current).HasUsbSelection && conflicts().Length > 0;
                        if (conflictingController) { stop?.Cancel(); Note("Conflicting controller detected; cancelling live control."); }
                        Telemetry reading;
                        if (testTelemetry is not null) reading = testTelemetry();
                        else { monitor ??= new(); reading = monitor.Read(Volatile.Read(ref current).PreferredGpuId); }
                        Volatile.Write(ref latest, history.Add(reading with { Fans = Fans.Values.OrderBy(f => f.Port).ToArray() }, Volatile.Read(ref current)));
                    }
                    else
                    {
                        double x = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 20d;
                        var preview = new Telemetry(DateTimeOffset.UtcNow, 48 + Math.Sin(x) * 5, 42 + Math.Cos(x) * 4, 12, 8, 31, "Simulated preview")
                        { CpuPowerWatts = 50 + Math.Sin(x) * 10, GpuPowerWatts = 35 + Math.Cos(x) * 12 };
                        Volatile.Write(ref latest, history.Add(preview, Volatile.Read(ref current)));
                    }
                    SetStatus("Sensors", (Latest.Warning.Length == 0 ? "CPU / GPU / RAM · shared sampler" : Latest.Warning) + $" · {seconds} s interval");
                }
                catch (Exception e)
                {
                    Volatile.Write(ref latest, new(DateTimeOffset.UtcNow, null, null, null, null, null, e.Message));
                    SetStatus("Sensors", "ERROR · " + e.Message); monitor?.Dispose(); monitor = null;
                }
                await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { monitor?.Dispose(); }
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
                        var telemetry = Latest with { Time = DateTimeOffset.Now };
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
                    try { lcd?.Dispose(); }
                    catch (Exception error)
                    {
                        releaseFailed = true;
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
                            throw new IOException("Temperature readiness timed out. No fan controller opened or speed command sent. Keep vendor cooling active. " + notReady.Reason);
                        SetStatus("Controller", "Waiting for valid temperatures · no fan-controller commands · " + notReady.Reason);
                        await Task.Delay(50, token);
                    }
                    Guard(token); controllerAttempted = true; controller = openController(s.ControllerSerial);
                    var lastDuty = new Dictionary<int, int>();
                    foreach (var p in monitored)
                    {
                        var initial = initialDecisions[p.Port];
                        controller.SetDuty(p.Port, initial.Duty!.Value); lastDuty[p.Port] = initial.Duty.Value;
                        FanCommands[p.Port] = new(p.Port, initial.Duty.Value, initial.TargetDuty ?? initial.Duty.Value, initial.Reason, DateTimeOffset.UtcNow);
                    }
                    SetStatus("Controller", "Connected · 264A:232B");
                    var failures = monitored.ToDictionary(p => p.Port, _ => 0);
                    var clock = Stopwatch.StartNew(); double nextControl = 0, nextLight = 0, previousControl = 0;
                    var lastLighting = new Dictionary<int, LedColor[]>(); double lastKeepalive = -2;
                    Settings? scheduleProfile = null; DateTime scheduleMinute = DateTime.MinValue;
                    var schedule = ScheduledLighting.Unchanged;
                    while (!token.IsCancellationRequested)
                    {
                        Guard(token);
                        s = Volatile.Read(ref current);
                        monitored = s.Ports.Where(p => s.CoolingEnabled && p.Mode != ControlMode.Preserve).ToArray();
                        // Turning lighting off is explicit user intent. Send black once;
                        // do not close/reinitialize the shared cooling controller.
                        foreach (var disabled in lastLighting.Keys.Where(port => !s.RgbEnabled || !s.Ports.Any(p => p.Port == port && Lighting.IsManaged(p))).ToArray())
                        {
                            controller.SetRgbFrame(disabled, new LedColor[lastLighting[disabled].Length]);
                            lastLighting.Remove(disabled);
                            SetStatus($"RGB port {disabled}", "Released · last output cleared; no continuing lighting writes");
                        }
                        // Explicit OFF owns lighting only; it does not imply RPM
                        // monitoring. Dark ports are still included in the
                        // managed lighting frame loop below.
                        foreach (var stalePort in Fans.Keys.Where(port => !s.Ports.Any(p => p.Port == port && (s.MonitorFanController || monitored.Any(m => m.Port == port) || s.RgbEnabled && p.Rgb))).ToArray())
                            Fans.TryRemove(stalePort, out _);
                        // Apply thermal decisions before any slower RPM queries or lighting transfers.
                        if (clock.Elapsed.TotalSeconds >= nextControl)
                        {
                            double controlTime = clock.Elapsed.TotalSeconds;
                            double controlSeconds = controlTime - previousControl;
                            previousControl = controlTime;
                            foreach (var p in monitored)
                            {
                                var decision = policies[p.Port].Evaluate(p, Latest, DateTimeOffset.UtcNow, controlSeconds);
                                if (decision.Duty is null) throw new IOException(decision.Reason);
                                if (lastDuty[p.Port] != decision.Duty!.Value)
                                { controller.SetDuty(p.Port, decision.Duty.Value); lastDuty[p.Port] = decision.Duty.Value; }
                                FanCommands[p.Port] = new(p.Port, decision.Duty.Value, decision.TargetDuty ?? decision.Duty.Value, decision.Reason, DateTimeOffset.UtcNow);
                                SetStatus($"Port {p.Port}", $"{decision.Duty}% · {decision.Reason}");
                            }
                            foreach (var p in s.Ports.Where(p => s.MonitorFanController || monitored.Any(m => m.Port == p.Port) || s.RgbEnabled && p.Rgb))
                            {
                                var reading = controller.Read(p.Port); Fans[p.Port] = reading;
                                if (failures.ContainsKey(p.Port))
                                {
                                    failures[p.Port] = reading.Rpm == 0 ? failures[p.Port] + 1 : 0;
                                    if (failures[p.Port] >= 3) throw new IOException($"Port {p.Port}: repeated zero RPM. Check wiring; releasing captured duty, not forcing full speed.");
                                }
                            }
                            nextControl = clock.Elapsed.TotalSeconds + Math.Min(s.PollSeconds, 2);
                        }
                        if (s.RgbEnabled && clock.Elapsed.TotalSeconds >= nextLight)
                        {
                            double elapsed = clock.Elapsed.TotalSeconds; bool keepalive = elapsed - lastKeepalive >= 2;
                            var local = DateTime.Now; var minute = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0);
                            if (!ReferenceEquals(scheduleProfile, s) || minute != scheduleMinute)
                            { schedule = RgbSchedule.At(s, local); scheduleProfile = s; scheduleMinute = minute; }
                            foreach (var p in s.Ports.Where(Lighting.IsManaged))
                            {
                                var colors = Lighting.OutputFrame(p, s.RgbColor, elapsed, Latest, schedule);
                                if (keepalive || !lastLighting.TryGetValue(p.Port, out var previous) || !previous.SequenceEqual(colors))
                                { controller.SetRgbFrame(p.Port, colors); lastLighting[p.Port] = colors; }
                                SetStatus($"RGB port {p.Port}", p.Rgb ? $"{schedule.Description} · {p.RgbEffect} · {p.LedCount} LEDs (commanded)" : "OFF · black output (commanded)");
                            }
                            if (keepalive) lastKeepalive = elapsed; nextLight = elapsed + .2;
                        }
                        // One bounded worker, at most 5 lighting frames/s; control/sensor cadence is independent.
                        await Task.Delay(s.RgbEnabled && s.Ports.Any(p => p.Rgb && p.RgbEffect != RgbEffect.Static) ? 200 : 1000, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    controllerFault = true;
                    SetStatus("Controller", controller is null
                        ? (controllerAttempted ? "FAULT LATCHED · connection attempt failed; ownership not established · " : "FAULT LATCHED · startup blocked before fan USB access · ") + e.Message
                        : "FAULT LATCHED · " + e.Message + " · captured duties requested back; no forced full-speed fallback or automatic reconnect. Restore vendor control.");
                    foreach (var p in monitored) SetStatus($"Port {p.Port}", controller is null
                        ? "NOT STARTED · no software speed ownership established" : "FAULT · release requested · verify vendor control");
                    Fans.Clear(); FanCommands.Clear();
                    break;
                }
                finally
                {
                    if (controller is not null)
                    {
                        var t = Latest;
                        controller.PrepareToClose(FanSafety.IsCritical(t, DateTimeOffset.UtcNow));
                        try { controller.Dispose(); }
                        catch (Exception releaseError)
                        { controllerFault = true; SetStatus("Controller", "FAULT · " + releaseError.Message); throw; }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void SetStatus(string key, string value)
    {
        if (!Status.TryGetValue(key, out var old) || old != value)
        {
            Status[key] = value;
            // Log state transitions/errors, not continuously changing temperatures/duty.
            if (value.StartsWith("ERROR") || value.StartsWith("FAULT") || key is "Controller" || key.Contains("panel") || key.Contains("AX")) Note(key + ": " + value);
        }
    }
    private void Note(string message)
    {
        log.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (log.Count > 250) log.TryDequeue(out _);
    }
}
