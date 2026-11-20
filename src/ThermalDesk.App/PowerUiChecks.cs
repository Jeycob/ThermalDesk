using System.Diagnostics;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class PowerUiChecks
{
    internal static int Run(string directory) => MainForm.ExercisePowerLifecycleForTest(directory);
}

internal sealed partial class MainForm
{
    // Invoke the very same handler as WndProc without broadcasting a power
    // event to Windows. Every sensor and USB controller below is injected.
    internal static int ExercisePowerLifecycleForTest(string directory)
    {
        int checks = 0;
        void Check(bool value, string message)
        { checks++; if (!value) throw new InvalidOperationException("Power UI: " + message); }
        void WaitFor(Func<bool> predicate, string message)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
            Check(predicate(), message);
        }
        static Telemetry Sample() => new(DateTimeOffset.UtcNow, 45, 40, 0, 0, 30);
        static void StopEngine(Engine target) => Task.Run(async () => await target.Stop()).GetAwaiter().GetResult();
        static bool SwitchRejected(MainForm form, int generation)
        {
            try { form.EnsureSessionSwitchAllowed(generation); return false; }
            catch (InvalidOperationException) { return true; }
        }
        var configured = new Settings { CoolingEnabled = true, CoolingWiringConfirmed = true };
        configured.Ports[0].Role = PortRole.Fan;
        configured.Ports[0].Mode = ControlMode.Fixed;
        configured.Ports[0].FixedDuty = 40;

        {
            var controller = new PowerUiController { FailDispose = true };
            var target = new Engine(_ => controller, Sample, () => []);
            using var form = new MainForm(configured.Copy(), Path.Combine(directory, "cleanup-failed-ui.json"), target);
            form.ShowOffscreenForTest();
            try
            {
                target.Start(configured, true);
                WaitFor(() => target.FanCommands.Count > 0, "ordinary cleanup test has active mock ownership");
                StopEngine(target); form.RefreshData();
                Check(target.CleanupFailed && !target.PowerPaused && !form.liveButton.Enabled && !form.previewButton.Enabled,
                    "ordinary cleanup failure blocks new sessions without claiming a sleep event");
                Check(form.session.Text.Contains("Hardware cleanup failed") && form.attention.Text.Contains("exit and reopen ThermalDesk") &&
                    !form.attention.Text.Contains("PAUSED AFTER SLEEP"),
                    "failed cleanup provides an explicit check-devices and reopen-app recovery action");
            }
            finally { StopEngine(target); }
        }

        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            int opens = 0;
            var first = new PowerUiController(entered, release);
            var second = new PowerUiController();
            var target = new Engine(_ => Interlocked.Increment(ref opens) == 1 ? first : second, Sample, () => []);
            using var form = new MainForm(configured.Copy(), Path.Combine(directory, "power-live-ui.json"), target);
            form.ShowOffscreenForTest();
            try
            {
                target.Start(configured, true);
                WaitFor(() => entered.IsSet, "mock USB read is in flight before suspend");
                int requestBeforeSleep = form.powerTransitionGeneration;
                var elapsed = Stopwatch.StartNew(); form.HandlePowerBroadcast(PowerSuspend); elapsed.Stop();
                Check(elapsed.ElapsedMilliseconds < 1800, "suspend handler returns within the Windows broadcast budget");
                Check(target.Cancelled && target.PowerPaused && target.Running && form.PowerCleanupPending,
                    "suspend cancels immediately but keeps ownership pending while USB read is blocked");
                Check(!form.liveButton.Enabled && !form.previewButton.Enabled && SwitchRejected(form, form.powerTransitionGeneration),
                    "Live and Preview remain disabled while the old worker owns hardware");
                int suspendedGeneration = form.powerTransitionGeneration;
                form.HandlePowerBroadcast(PowerSuspend);
                form.HandlePowerBroadcast(PowerResumeAutomatic); form.HandlePowerBroadcast(PowerResumeSuspend);
                Check(form.powerTransitionGeneration == suspendedGeneration && opens == 1 && form.PowerCleanupPending,
                    "duplicate suspend and both resume notifications cannot create another session");
                release.Set();
                WaitFor(() => !target.Running && form.liveButton.Enabled, "completed release enables an explicit new start");
                form.RefreshData();
                Check(!target.Live && target.PowerPaused && first.Disposals == 1 && opens == 1 &&
                    form.session.Text.Contains("Paused after sleep") && !form.session.Text.Contains("PREVIEW"),
                    "resume stays paused without simulated readings or automatic reconnection");
                Check(SwitchRejected(form, requestBeforeSleep), "a request confirmed before sleep cannot continue after release");
                form.EnsureSessionSwitchAllowed(form.powerTransitionGeneration);
                target.Start(configured, true);
                WaitFor(() => opens == 2, "a new explicit start may open the controller after release");
                form.HandlePowerBroadcast(PowerResumeSuspend); form.HandlePowerBroadcast(PowerResumeAutomatic);
                Application.DoEvents(); form.RefreshData();
                Check(target.Running && target.Live && !target.Cancelled && !target.PowerPaused && opens == 2,
                    "late duplicate resume notifications leave the new manual session running");
            }
            finally { release.Set(); StopEngine(target); }
        }

        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var controller = new PowerUiController(entered, release);
            var target = new Engine(_ => controller, Sample, () => []);
            using var form = new MainForm(configured.Copy(), Path.Combine(directory, "power-close-ui.json"), target);
            form.ShowOffscreenForTest();
            try
            {
                target.Start(configured, true);
                WaitFor(() => entered.IsSet, "close test has a pending mock USB transaction");
                form.HandlePowerBroadcast(PowerSuspend); form.HandlePowerBroadcast(PowerResumeAutomatic);
                form.RequestExit(); Application.DoEvents();
                Check(!form.IsDisposed && target.Running && form.PowerCleanupPending,
                    "Exit waits asynchronously for old power cleanup before disposing the form");
                release.Set();
                WaitFor(() => form.IsDisposed, "Exit completes after old workers release their handles");
                Check(!target.Running && controller.Disposals == 1, "close during power cleanup releases the controller once");
            }
            finally { release.Set(); StopEngine(target); }
        }

        {
            var controller = new PowerUiController { FailRead = true };
            var target = new Engine(_ => controller, Sample, () => []);
            using var form = new MainForm(configured.Copy(), Path.Combine(directory, "power-fault-ui.json"), target);
            form.ShowOffscreenForTest();
            try
            {
                target.Start(configured, true);
                WaitFor(() => target.ControllerFault && controller.Disposals == 1, "a real mock controller fault is latched before sleep");
                var prior = target.LastControllerFault;
                form.HandlePowerBroadcast(PowerSuspend); form.HandlePowerBroadcast(PowerResumeAutomatic);
                WaitFor(() => !target.Running, "faulted session also finishes power cleanup");
                form.RefreshData();
                Check(target.ControllerFault && ReferenceEquals(prior, target.LastControllerFault) && form.session.Text.Contains("FAULT"),
                    "power pause preserves the earlier fault and its diagnostic evidence");
            }
            finally { StopEngine(target); }
        }

        {
            int opens = 0;
            var target = new Engine(_ => { opens++; return new PowerUiController(); }, Sample, () => []);
            using var form = new MainForm(configured.Copy(), Path.Combine(directory, "power-before-shown.json"), target) { StartupRequested = true };
            form.HandlePowerBroadcast(PowerSuspend);
            Check(form.StartInitialSession() is null && !target.Running && opens == 0,
                "suspend before Shown blocks saved automatic Live startup");
            form.HandlePowerBroadcast(PowerResumeAutomatic); form.HandlePowerBroadcast(PowerResumeSuspend);
            Check(form.StartInitialSession() is null && !target.Running && opens == 0,
                "initial startup cannot manufacture Preview or Live after a power transition");
        }

        {
            int opens = 0;
            var target = new Engine(_ => { opens++; return new PowerUiController(); }, Sample, () => []);
            using var form = new MainForm(new Settings(), Path.Combine(directory, "power-preview-ui.json"), target);
            form.ShowOffscreenForTest();
            try
            {
                target.Start(new Settings(), false);
                form.HandlePowerBroadcast(PowerSuspend); form.HandlePowerBroadcast(PowerResumeAutomatic);
                WaitFor(() => !target.Running, "an existing Preview session also stops for sleep");
                form.RefreshData();
                Check(!target.Live && target.PowerPaused && opens == 0 && !form.session.Text.Contains("PREVIEW"),
                    "resume never restarts a stopped simulation as recovery");
            }
            finally { StopEngine(target); }
        }
        return checks;
    }

    private sealed class PowerUiController(ManualResetEventSlim? entered = null, ManualResetEventSlim? release = null) : IFanController
    {
        internal int Disposals;
        internal bool FailRead, FailDispose;
        public void SetDuty(int port, int duty) { }
        public void SetRgbFrame(int port, IReadOnlyList<LedColor> colors) { }
        public FanReading Read(int port)
        {
            if (FailRead) throw new IOException("Mock USB read failure before sleep");
            entered?.Set(); release?.Wait(); return new(port, 900, 40);
        }
        public void PrepareToClose(bool criticalTemperature) { }
        public void Dispose()
        {
            Interlocked.Increment(ref Disposals);
            if (FailDispose) throw new IOException("Mock controller release failure");
        }
    }
}
