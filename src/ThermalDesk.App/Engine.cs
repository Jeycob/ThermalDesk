using System.Collections.Concurrent;
using System.Diagnostics;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class Engine
{
    private CancellationTokenSource? stop;
    private Task[] tasks = [];
    private Telemetry latest = new(DateTimeOffset.MinValue, null, null, null, null, null);
    private readonly ConcurrentQueue<string> log = new();
    private volatile bool conflictingController;
    public Telemetry Latest => Volatile.Read(ref latest);
    public ConcurrentDictionary<string, string> Status { get; } = new();
    public ConcurrentDictionary<int, FanReading> Fans { get; } = new();
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
        if (live && Conflicts().Length != 0) throw new InvalidOperationException("Close conflicting hardware-control applications: " + string.Join(", ", Conflicts()));
        var s = settings.Copy(); stop = new(); var token = stop.Token; Live = live; conflictingController = false;
        Status.Clear(); Fans.Clear();
        latest = new(DateTimeOffset.MinValue, null, null, null, null, null);
        Status["Session"] = live ? "LIVE · configured devices only" : "PREVIEW · no hardware access";
        Note(live ? "Live session started. No flash writes or firmware updates." : "Preview started. All values are simulated.");
        var workers = new List<Task> { Task.Run(() => SensorLoop(s.PollSeconds, live, token)) };
        if (live)
        {
            if (s.Panel.Enabled) workers.Add(Task.Run(() => LcdLoop("480×128 panel", s.Panel, true, s.PollSeconds, token)));
            if (s.Aio.Enabled) workers.Add(Task.Run(() => LcdLoop("TOUGHLIQUID AX", s.Aio, false, s.PollSeconds, token)));
            if (s.CoolingEnabled || s.RgbEnabled) workers.Add(Task.Run(() => FanLoop(s, token)));
        }
        tasks = workers.ToArray();
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
        Status["Session"] = "Stopped"; Note("Stopped. Managed USB fan ports were requested to run at 100%; this is not BIOS hand-back.");
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
                        conflictingController = Conflicts().Length > 0;
                        if (conflictingController) { stop?.Cancel(); Note("Conflicting controller detected; cancelling live control."); }
                        monitor ??= new();
                        Volatile.Write(ref latest, monitor.Read());
                    }
                    else
                    {
                        double x = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 20d;
                        Volatile.Write(ref latest, new(DateTimeOffset.UtcNow, 48 + Math.Sin(x) * 5, 42 + Math.Cos(x) * 4, 12, 8, 31, "Simulated preview"));
                    }
                    SetStatus("Sensors", Latest.Warning.Length == 0 ? "CPU / GPU / RAM · shared sampler" : Latest.Warning);
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
                        using var image = renderer.Render(Latest, settings, 480, panel ? 128 : 480);
                        lcd.Send(FrameRenderer.Jpeg(image)); lcd.Heartbeat();
                        // Heartbeats stay at <= 2 s even when sensor polling is slower.
                        await Task.Delay(TimeSpan.FromSeconds(1d / settings.FramesPerSecond), token);
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
                FanController? controller = null;
                try
                {
                    Guard(token); controller = new(s.ControllerSerial);
                    foreach (var p in monitored) { policies[p.Port].Reset(); controller.SetDuty(p.Port, 100); }
                    SetStatus("Controller", "Connected · 264A:232B");
                    var failures = monitored.ToDictionary(p => p.Port, _ => 0);
                    while (!token.IsCancellationRequested)
                    {
                        Guard(token);
                        // Apply thermal decisions before any slower RPM queries or lighting transfers.
                        foreach (var p in monitored)
                        {
                            var decision = policies[p.Port].Evaluate(p, Latest, DateTimeOffset.UtcNow, s.PollSeconds);
                            controller.SetDuty(p.Port, decision.Duty!.Value);
                            SetStatus($"Port {p.Port}", $"{decision.Duty}% · {decision.Reason}");
                        }
                        if (s.RgbEnabled) foreach (var p in s.Ports.Where(p => p.Rgb)) controller.SetRgb(p, s.RgbColor);
                        foreach (var p in monitored)
                        {
                            var reading = controller.Read(p.Port); Fans[p.Port] = reading;
                            failures[p.Port] = reading.Rpm == 0 ? failures[p.Port] + 1 : 0;
                            if (failures[p.Port] >= 3) throw new IOException($"Port {p.Port}: repeated zero RPM. Check wiring; requesting safe duty.");
                        }
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(s.PollSeconds, 2)), token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception e) { SetStatus("Controller", "ERROR · " + e.Message); }
                finally { controller?.Dispose(); }
                await Task.Delay(TimeSpan.FromSeconds(5), token);
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
            if (value.StartsWith("ERROR") || key is "Controller" || key.Contains("panel") || key.Contains("AX")) Note(key + ": " + value);
        }
    }
    private void Note(string message)
    {
        log.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (log.Count > 250) log.TryDequeue(out _);
    }
}
