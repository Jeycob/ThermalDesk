using System.Collections.Concurrent;
using System.Diagnostics;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class Engine
{
    private readonly Func<string, IFanController> openController;
    private readonly Func<Telemetry>? testTelemetry;
    private readonly Func<string[]> conflicts;
    public Engine() : this(serial => new FanController(serial), null, Conflicts) { }
    internal Engine(Func<string, IFanController> controllerFactory, Func<Telemetry>? telemetryReader, Func<string[]> findConflicts)
    { openController = controllerFactory; testTelemetry = telemetryReader; conflicts = findConflicts; }
    private CancellationTokenSource? stop;
    private Task[] tasks = [];
    private Telemetry latest = new(DateTimeOffset.MinValue, null, null, null, null, null);
    private readonly ConcurrentQueue<string> log = new();
    private volatile bool conflictingController;
    private Settings current = new();
    private volatile bool controllerFault;
    public bool ControllerFault => controllerFault;
    public Telemetry Latest => Volatile.Read(ref latest);
    public ConcurrentDictionary<string, string> Status { get; } = new();
    public ConcurrentDictionary<int, FanReading> Fans { get; } = new();
    public ConcurrentDictionary<int, FanControlState> FanCommands { get; } = new();
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
        if (live && conflicts().Length != 0) throw new InvalidOperationException("Close conflicting hardware-control applications: " + string.Join(", ", conflicts()));
        var s = settings.Copy(); Volatile.Write(ref current, s); stop = new(); var token = stop.Token; Live = live; conflictingController = false; controllerFault = false;
        Status.Clear(); Fans.Clear(); FanCommands.Clear();
        latest = new(DateTimeOffset.MinValue, null, null, null, null, null);
        Status["Session"] = live ? "LIVE · configured devices only" : "PREVIEW · no hardware access";
        Status["TOUGHLIQUID AX"] = s.Aio.Enabled ? "Enabled · waiting for connection" : "Disabled · enable in Displays";
        Status["480×128 panel"] = s.Panel.Enabled ? "Enabled · waiting for connection" : "Disabled · enable in Displays";
        Note(live ? "Live session started. No flash writes or firmware updates." : "Preview started. All values are simulated.");
        var workers = new List<Task> { Task.Run(() => SensorLoop(SensorInterval(s), live, token)) };
        if (live)
        {
            if (s.Panel.Enabled) workers.Add(Task.Run(() => LcdLoop("480×128 panel", s.Panel, true, s.PollSeconds, token)));
            if (s.Aio.Enabled) workers.Add(Task.Run(() => LcdLoop("TOUGHLIQUID AX", s.Aio, false, s.PollSeconds, token)));
            if (s.CoolingEnabled || s.RgbEnabled || s.MonitorFanController) workers.Add(Task.Run(() => FanLoop(s, token)));
        }
        tasks = workers.ToArray();
    }
    public void ApplyLiveSettings(Settings settings)
    {
        if (!Running || !Live || Cancelled || ControllerFault) throw new InvalidOperationException("No healthy live session. Stop and resolve the fault before reconnecting.");
        settings.ValidateLiveSelection();
        var before = Volatile.Read(ref current); var next = settings.Copy();
        bool SamePorts() => before.Ports.All(p =>
        {
            var other = next.Ports.Single(x => x.Port == p.Port);
            return p.Role == other.Role && (p.Mode == ControlMode.Preserve) == (other.Mode == ControlMode.Preserve);
        });
        if (before.ControllerSerial != next.ControllerSerial || before.CoolingEnabled != next.CoolingEnabled || before.RgbEnabled != next.RgbEnabled ||
            before.MonitorFanController != next.MonitorFanController || before.PollSeconds != next.PollSeconds || !SamePorts() ||
            before.Aio.Enabled != next.Aio.Enabled || before.Panel.Enabled != next.Panel.Enabled ||
            before.Aio.Brightness != next.Aio.Brightness || before.Panel.Brightness != next.Panel.Brightness)
            throw new InvalidOperationException("Changing devices, managed ports/wiring, polling or LCD brightness requires Stop first. Curve, RGB and widget edits can be applied without restarting fans.");
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
        Volatile.Write(ref latest, Latest with { Fans = [] });
        foreach (var key in Status.Keys.Where(k => k.StartsWith("Port ", StringComparison.Ordinal))) Status[key] = "Released · no live command / feedback";
        if (!controllerFault && Status.ContainsKey("Controller")) Status["Controller"] = "Released · no live controller feedback";
        Status["Session"] = "Stopped"; Note("Stopped. Captured fan duties requested back, except a real critical-temperature override. Restoration is not BIOS hand-back or a guaranteed hardware watchdog.");
    }
    private async Task SensorLoop(int seconds, bool live, CancellationToken token)
    {
        Monitoring? monitor = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (live)
                    {
                        conflictingController = conflicts().Length > 0;
                        if (conflictingController) { stop?.Cancel(); Note("Conflicting controller detected; cancelling live control."); }
                        Telemetry reading;
                        if (testTelemetry is not null) reading = testTelemetry();
                        else { monitor ??= new(); reading = monitor.Read(); }
                        Volatile.Write(ref latest, reading with { Fans = Fans.Values.OrderBy(f => f.Port).ToArray() });
                    }
                    else
                    {
                        double x = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 20d;
                        Volatile.Write(ref latest, new(DateTimeOffset.UtcNow, 48 + Math.Sin(x) * 5, 42 + Math.Cos(x) * 4, 12, 8, 31, "Simulated preview"));
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
    private void Guard(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (conflictingController)
        {
            stop?.Cancel();
            throw new IOException("Another hardware controller started. Session cancelled to avoid conflicting writes.");
        }
    }
    private async Task LcdLoop(string name, DisplaySettings settings, bool panel, int seconds, CancellationToken token)
    {
        using var renderer = new FrameRenderer();
        try
        {
            while (!token.IsCancellationRequested)
            {
                ILcd? lcd = null;
                try
                {
                    Guard(token); lcd = panel ? new PanelLcd(settings.Brightness) : new AioLcd(settings.Brightness);
                    SetStatus(name, "Connected · " + settings.Content);
                    while (!token.IsCancellationRequested)
                    {
                        Guard(token);
                        var active = Volatile.Read(ref current); var display = panel ? active.Panel : active.Aio;
                        using var image = renderer.Render(Latest, display, 480, panel ? 128 : 480);
                        lcd.Send(FrameRenderer.Jpeg(image)); lcd.Heartbeat();
                        // Heartbeats stay at <= 2 s even when sensor polling is slower.
                        await Task.Delay(TimeSpan.FromSeconds(1d / display.FramesPerSecond), token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception e) { SetStatus(name, "ERROR · " + e.Message); }
                finally { lcd?.Dispose(); }
                await Task.Delay(TimeSpan.FromSeconds(5), token);
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
                try
                {
                    // Let the shared sampler publish its first reading before taking control.
                    // A healthy start uses the real curve. Missing initial sensor data
                    // aborts before opening the controller; it is not a full-speed command.
                    var startupWait = Stopwatch.StartNew();
                    while (monitored.Length > 0 && Latest.Time == DateTimeOffset.MinValue && startupWait.Elapsed.TotalSeconds < 4)
                    { Guard(token); await Task.Delay(50, token); }
                    var initialDecisions = monitored.ToDictionary(p => p.Port, p => policies[p.Port].Evaluate(p, Latest, DateTimeOffset.UtcNow, s.PollSeconds));
                    if (initialDecisions.Values.Any(x => x.Duty is null)) throw new IOException("Initial temperature unavailable. Controller left untouched; keep vendor cooling active.");
                    Guard(token); controller = openController(s.ControllerSerial);
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
                    while (!token.IsCancellationRequested)
                    {
                        Guard(token);
                        s = Volatile.Read(ref current);
                        monitored = s.Ports.Where(p => s.CoolingEnabled && p.Mode != ControlMode.Preserve).ToArray();
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
                            foreach (var p in s.Ports.Where(p => p.Rgb))
                            {
                                var colors = Lighting.Frame(p, s.RgbColor, elapsed, Latest);
                                if (keepalive || !lastLighting.TryGetValue(p.Port, out var previous) || !previous.SequenceEqual(colors))
                                { controller.SetRgbFrame(p.Port, colors); lastLighting[p.Port] = colors; }
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
                    SetStatus("Controller", "FAULT LATCHED · " + e.Message + " · captured duties requested back; no forced full-speed fallback or automatic reconnect. Restore vendor control.");
                    foreach (var p in monitored) SetStatus($"Port {p.Port}", "FAULT · release requested · verify vendor control");
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
