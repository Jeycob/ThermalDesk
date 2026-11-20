namespace ThermalDesk.App;

internal sealed partial class MainForm
{
    private const int PowerBroadcastMessage = 0x0218, PowerSuspend = 0x0004,
        PowerResumeSuspend = 0x0007, PowerResumeAutomatic = 0x0012;
    private const int SuspendReleaseBudgetMilliseconds = 1000;
    private Task? powerPauseTask;
    private bool systemSuspended;
    private int powerTransitionGeneration;
    private string powerPauseFailure = "";

    private bool PowerCleanupPending => powerPauseTask is { IsCompleted: false };
    private bool PowerSwitchBlocked => systemSuspended || PowerCleanupPending || engine.CleanupFailed ||
        powerPauseTask is { IsFaulted: true } || powerPauseFailure.Length > 0;
    private string PowerSessionMessage => powerPauseFailure.Length > 0
        ? "Hardware cleanup failed · check devices, then exit and reopen ThermalDesk · " + powerPauseFailure
        : engine.CleanupFailed ? "Hardware cleanup failed · check devices, then exit and reopen ThermalDesk · " + engine.CleanupStatus
        : PowerCleanupPending ? "PAUSING FOR SLEEP · waiting for hardware release; do not reconnect"
        : systemSuspended ? "PAUSED FOR SYSTEM SLEEP · devices are stopped"
        : "Paused after sleep · Start live control";

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == PowerBroadcastMessage && engine is not null)
            HandlePowerBroadcast(unchecked((int)message.WParam.ToInt64()));
        base.WndProc(ref message);
    }

    private void HandlePowerBroadcast(int reason)
    {
        if (reason == PowerSuspend)
        {
            if (!systemSuspended) powerTransitionGeneration++;
            systemSuspended = true;
            if (engine.Running)
            {
                try
                {
                    var pending = engine.PauseForSystemSleep();
                    if (!ReferenceEquals(pending, powerPauseTask))
                    {
                        powerPauseTask = pending; powerPauseFailure = "";
                        _ = ObservePowerPause(pending);
                    }
                    // Engine finalization is independent of the UI context.
                    // Give bounded release work a chance before Windows removes
                    // USB power, while staying inside the two-second broadcast
                    // budget. A timeout keeps the old session pending/blocked.
                    if (!pending.IsCompleted) pending.Wait(SuspendReleaseBudgetMilliseconds);
                }
                catch (Exception error)
                {
                    powerPauseFailure = error.GetBaseException().Message;
                }
            }
            UpdatePowerSessionButtons();
            QueuePowerUiRefresh();
        }
        else if (reason is PowerResumeSuspend or PowerResumeAutomatic)
        {
            // Windows can send both notifications. Neither authorizes opening
            // hardware, starting Preview, or stopping a later manual session.
            bool wasSuspended = systemSuspended;
            systemSuspended = false;
            if (wasSuspended) engine.NotifySystemResume();
            UpdatePowerSessionButtons();
            QueuePowerUiRefresh();
        }
    }

    private async Task ObservePowerPause(Task pending)
    {
        string failure = "";
        try { await pending.ConfigureAwait(false); }
        catch (Exception error) { failure = error.GetBaseException().Message; }
        QueuePowerUiRefresh(() =>
        {
            if (ReferenceEquals(powerPauseTask, pending) && failure.Length > 0)
                powerPauseFailure = failure;
        });
    }

    private void QueuePowerUiRefresh(Action? beforeRefresh = null)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed || Disposing) return;
                beforeRefresh?.Invoke();
                RefreshData();
            }));
        }
        catch (InvalidOperationException) { /* Handle destroyed during close. */ }
    }

    private void UpdatePowerSessionButtons()
    {
        // These fields can still be uninitialized if Windows broadcasts while
        // the form handle is being constructed.
        if (liveButton is not null)
        {
            SetText(liveButton, engine.CleanupFailed || powerPauseFailure.Length > 0 ? "Cleanup failed · restart app"
                : PowerCleanupPending ? "Waiting for hardware release" : engine.ControllerFault && engine.Running
                ? "Blocked · Stop before retry" : engine.Running && engine.Live ? "Running · automatic"
                : settings.AllowMonitoringOnly && !settings.HasUsbSelection ? "Start monitoring" : "Start live control");
            liveButton.Enabled = !PowerSwitchBlocked && (!engine.Running || !engine.Live);
        }
        if (previewButton is not null) previewButton.Enabled = !PowerSwitchBlocked;
    }

    private void EnsureSessionSwitchAllowed(int expectedPowerGeneration)
    {
        if (expectedPowerGeneration != powerTransitionGeneration)
            throw new InvalidOperationException("System sleep interrupted this request. After hardware release finishes, start the desired session explicitly.");
        if (engine.CleanupFailed || powerPauseFailure.Length > 0 || powerPauseTask is { IsFaulted: true })
            throw new InvalidOperationException("Hardware cleanup failed. Check the devices, then exit and reopen ThermalDesk before starting another session.");
        if (PowerSwitchBlocked)
            throw new InvalidOperationException("System power transition is still pending. Wait for hardware release before starting another session.");
    }
}
