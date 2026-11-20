using System.Diagnostics;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// Exercises lifecycle ownership with injected sensors/transports only.
internal static class PowerLifecycleChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value, string name) { count++; if (!value) throw new InvalidOperationException("Power lifecycle: " + name); }
        bool Wait(Func<bool> predicate, int milliseconds = 5000) => SpinWait.SpinUntil(predicate, milliseconds);
        static Telemetry Sample() => new(DateTimeOffset.UtcNow, 50, 40, 0, 0, 30);
        static Settings Profile()
        {
            var settings = new Settings { CoolingEnabled = true, CoolingWiringConfirmed = true, PollSeconds = 1 };
            settings.Ports[0].Role = PortRole.Fan; settings.Ports[0].Mode = ControlMode.Fixed; settings.Ports[0].FixedDuty = 40;
            return settings;
        }
        static void Complete(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

        using var readEntered = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        var blocked = new TestController { ReadEntered = readEntered, ReleaseRead = releaseRead };
        var next = new TestController(); int opens = 0;
        var engine = new Engine(_ => Interlocked.Increment(ref opens) == 1 ? blocked : next, Sample, () => [], stopWaitTimeout: TimeSpan.FromMilliseconds(80));
        var profile = Profile();
        try
        {
            engine.Start(profile, true);
            Check(readEntered.Wait(3000), "controller is inside an old in-flight read before suspend");
            var watch = Stopwatch.StartNew();
            var paused = engine.PauseForSystemSleep();
            Check(engine.Cancelled && engine.PowerPaused && watch.Elapsed < TimeSpan.FromSeconds(1), "suspend flags cancellation synchronously without waiting for native IO");
            Check(engine.Running && !paused.IsCompleted, "old workers retain the session while read is pending");
            Check(ReferenceEquals(paused, engine.PauseForSystemSleep()), "duplicate suspend shares the actual shutdown completion");
            bool timedOut = false;
            try { engine.Stop().GetAwaiter().GetResult(); } catch (TimeoutException) { timedOut = true; }
            Check(timedOut && engine.Running && !paused.IsCompleted, "manual Stop timeout ends only its waiter, not the ownership epoch");
            bool liveBlocked = false, previewBlocked = false;
            try { engine.Start(profile, true); } catch (InvalidOperationException) { liveBlocked = true; }
            try { engine.Start(profile, false); } catch (InvalidOperationException) { previewBlocked = true; }
            Check(liveBlocked && previewBlocked && opens == 1, "no new Live or Preview session can replace pending old workers");
            releaseRead.Set(); Complete(paused);
            Check(!engine.Running && engine.PowerPaused && !engine.CleanupFailed, "successful cleanup leaves the engine paused and stopped");
            Check(blocked.Disposals == 1 && blocked.Preparations == 1 && opens == 1, "concurrent Stop and suspend cause one preparation and one close, without reconnect");
            Complete(engine.Stop());
            Check(blocked.Disposals == 1 && engine.PowerPaused && engine.CleanupStatus.StartsWith("Completed"), "repeated Stop preserves completed cleanup and power pause");
            Check(engine.Logs.Count(line => line.Contains("Windows suspend requested")) == 1, "duplicate notifications do not duplicate the power transition log");
            engine.NotifySystemResume();
            Check(engine.PowerPaused && !engine.Running && opens == 1, "resume notification never restarts hardware or clears the pause");
            engine.Start(profile, true);
            Check(Wait(() => next.Writes > 0) && !engine.PowerPaused && opens == 2, "only an explicit new Start begins a fresh generation and clears power pause");
        }
        finally { releaseRead.Set(); Complete(engine.PauseForSystemSleep()); }
        Check(next.Disposals == 1, "the explicit next generation also closes exactly once");

        using var closeEntered = new ManualResetEventSlim();
        using var releaseClose = new ManualResetEventSlim();
        var failed = new TestController { FailRead = true, FailClose = true, CloseEntered = closeEntered, ReleaseClose = releaseClose };
        var faulty = new Engine(_ => failed, Sample, () => []);
        try
        {
            faulty.Start(Profile(), true);
            Check(closeEntered.Wait(3000), "original USB fault reaches a pending restore/close");
            var original = faulty.LastControllerFault ?? throw new InvalidOperationException("Missing original controller fault");
            Check(original.Reason.Contains("original USB fault") && faulty.Status["Controller"].Contains("release pending"), "UI reports restore pending before the close call returns");
            var paused = faulty.PauseForSystemSleep();
            Check(!paused.IsCompleted && faulty.Running, "a pending close also prevents a new ownership epoch");
            releaseClose.Set(); Complete(paused);
            Check(!faulty.Running && faulty.PowerPaused && faulty.CleanupFailed && faulty.ControllerFault, "failed cleanup completes the old workers but remains explicitly failed");
            Check(ReferenceEquals(original, faulty.LastControllerFault) && faulty.CleanupStatus.Contains("restore failure"), "cleanup failure is retained alongside, without replacing the triggering fault");
            Check(faulty.Status["Controller"].Contains("original USB fault") && faulty.Status["Controller"].Contains("restore failure") &&
                faulty.Status["Port 1"].Contains("unconfirmed"), "stop does not replace failed restoration with a Released claim");
            faulty.NotifySystemResume(); Complete(faulty.Stop());
            Check(failed.Disposals == 1 && ReferenceEquals(original, faulty.LastControllerFault), "resume and repeated Stop do not retry disposal or clear a pre-existing fault");
        }
        finally { releaseClose.Set(); Complete(faulty.PauseForSystemSleep()); }

        var badPreparation = new TestController { FailPreparation = true };
        var preparing = new Engine(_ => badPreparation, Sample, () => []);
        try
        {
            preparing.Start(Profile(), true);
            Check(Wait(() => badPreparation.Writes > 0), "preparation failure fixture first establishes ownership");
            Complete(preparing.PauseForSystemSleep());
            Check(badPreparation.Preparations == 1 && badPreparation.Disposals == 1 && preparing.CleanupFailed,
                "failed preparation still attempts the one required close and preserves failure evidence");
        }
        finally { Complete(preparing.PauseForSystemSleep()); }

        var lcd = new TestLcd();
        var displays = new Settings { CoolingEnabled = false, RgbEnabled = false, MonitorFanController = false };
        displays.Aio.Enabled = true;
        var displayEngine = new Engine(_ => throw new InvalidOperationException("Unexpected fan access"), Sample, () => [], displayFactory: (_, _) => lcd);
        try
        {
            displayEngine.Start(displays, true);
            Check(Wait(() => lcd.Frames > 0), "mock LCD is active before suspend");
            Complete(displayEngine.PauseForSystemSleep());
            Check(!displayEngine.Running && displayEngine.CleanupFailed && lcd.Disposals == 1,
                "LCD close failure is retained even when its worker catches the exception and returns normally");
            Check(displayEngine.CleanupStatus.Contains("LCD close failure") && displayEngine.Status["TOUGHLIQUID AX"].Contains("retry blocked"),
                "LCD release failure remains visible after session stop");
        }
        finally { Complete(displayEngine.PauseForSystemSleep()); }

        var healthyLcd = new TestLcd { FailClose = false };
        var healthyDisplayEngine = new Engine(_ => throw new InvalidOperationException("Unexpected fan access"), Sample, () => [], displayFactory: (_, _) => healthyLcd);
        try
        {
            healthyDisplayEngine.Start(displays, true);
            Check(Wait(() => healthyLcd.Frames > 0), "healthy mock LCD is connected before pause");
            Complete(healthyDisplayEngine.PauseForSystemSleep());
            Check(healthyLcd.Disposals == 1 && !healthyDisplayEngine.CleanupFailed && healthyDisplayEngine.Status["TOUGHLIQUID AX"].StartsWith("Released"),
                "successful LCD close no longer leaves a misleading Connected state after pause");
        }
        finally { Complete(healthyDisplayEngine.PauseForSystemSleep()); }

        using var validationEntered = new ManualResetEventSlim();
        using var releaseValidation = new ManualResetEventSlim();
        int lateOpens = 0;
        var validating = new Engine(_ => { Interlocked.Increment(ref lateOpens); return new TestController(); }, Sample, () =>
        {
            validationEntered.Set(); releaseValidation.Wait(); return [];
        });
        var lateStart = Task.Run(() =>
        {
            try { validating.Start(Profile(), true); return false; }
            catch (InvalidOperationException) { return true; }
        });
        try
        {
            Check(validationEntered.Wait(3000), "start is held in preflight before any workers or hardware open");
            Complete(validating.PauseForSystemSleep());
            releaseValidation.Set();
            Check(lateStart.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult() && validating.PowerPaused && !validating.Running && lateOpens == 0,
                "suspend invalidates a concurrent preflight so it cannot start hardware after cancellation");
        }
        finally { releaseValidation.Set(); Complete(lateStart); Complete(validating.PauseForSystemSleep()); }
        return count;
    }

    private sealed class TestController : IFanController
    {
        internal ManualResetEventSlim? ReadEntered, ReleaseRead, CloseEntered, ReleaseClose;
        internal bool FailRead, FailClose, FailPreparation;
        internal int Writes, Preparations, Disposals;
        private int duty = 40;
        public void SetDuty(int port, int value) { Volatile.Write(ref duty, value); Interlocked.Increment(ref Writes); }
        public void SetRgbFrame(int port, IReadOnlyList<LedColor> colors) { }
        public FanReading Read(int port)
        {
            if (FailRead) throw new IOException("Injected original USB fault");
            ReadEntered?.Set(); ReleaseRead?.Wait();
            return new(port, 900, Volatile.Read(ref duty));
        }
        public void PrepareToClose(bool criticalTemperature)
        {
            Interlocked.Increment(ref Preparations);
            if (FailPreparation) throw new IOException("Injected preparation failure");
        }
        public void Dispose()
        {
            Interlocked.Increment(ref Disposals); CloseEntered?.Set(); ReleaseClose?.Wait();
            if (FailClose) throw new IOException("Injected restore failure");
        }
    }
    private sealed class TestLcd : ILcd
    {
        internal int Frames, Disposals;
        internal bool FailClose = true;
        public void Send(byte[] jpeg) => Interlocked.Increment(ref Frames);
        public void Heartbeat() { }
        public void Dispose() { Interlocked.Increment(ref Disposals); if (FailClose) throw new IOException("Injected LCD close failure"); }
    }
}
