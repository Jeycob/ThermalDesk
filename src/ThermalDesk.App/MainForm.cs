using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(10, 17, 29), Surface = Color.FromArgb(22, 34, 48), Accent = Color.FromArgb(38, 218, 179), Muted = Color.FromArgb(151, 172, 191);
    private readonly Engine engine;
    private readonly string configPath;
    private string startupWarning;
    private Settings settings;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer settingsDelay = new() { Interval = 450 };
    private string applyHint = "";
    private readonly Icon appIcon = AppIcon.Create();
    private readonly NotifyIcon tray = new() { Text = "ThermalDesk" };
    private readonly ToolTip hints = new() { AutoPopDelay = 20000, InitialDelay = 450, ReshowDelay = 100, ShowAlways = true };
    private readonly Label session = new(), resources = new(), cpu = new(), gpu = new(), memory = new();
    private readonly TextBox deviceStatus = new(), log = new(), color = new(), serial = new();
    private readonly PictureBox panelPreview = new(), aioPreview = new();
    private readonly DataGridView ports = new BufferedDataGridView();
    private readonly Dictionary<int, PortSettings> portDesign = new();
    private readonly CheckBox monitorFans = new() { Text = "Monitor controller RPM (no speed writes)", AutoSize = true };
    private readonly CheckBox minimizeAfterStartup = new() { Text = "Minimize after startup", AutoSize = true }, minimizeWhenClosed = new() { Text = "Minimize to tray when closed", AutoSize = true };
    private readonly CheckBox resumeLastProfile = new() { Text = "Resume my saved profile when ThermalDesk opens", AutoSize = true };
    private readonly CheckBox technicalDetails = new() { Text = "Show technical resource counters", AutoSize = true };
    private readonly CheckBox processMonitoring = new() { Text = "Enable CPU/GPU process activity monitoring (Live only)", AutoSize = true };
    private readonly NumericUpDown processInterval = new() { Minimum = 5, Maximum = 60, Width = 80 };
    private readonly NumericUpDown logInterval = new() { Minimum = 5, Maximum = 60, Width = 80 };
    private readonly NumericUpDown powerExtra = new() { Minimum = 0, Maximum = 500, DecimalPlaces = 1, Width = 80 }, powerEfficiency = new() { Minimum = 50, Maximum = 100, DecimalPlaces = 1, Width = 80 };
    private readonly CheckBox powerGpu = new() { Text = "Include selected GPU in total estimate (known integrated GPUs are excluded)", AutoSize = true };
    private readonly TextBox processActivityLog = new();
    private readonly Label processActivityStatus = new() { Dock = DockStyle.Top, Height = 110 };
    private readonly CheckBox cooling = new() { Text = "Enable USB fan/pump control" }, wiring = new() { Text = "I verified every managed port and independent hardware protection" }, rgb = new() { Text = "Enable RGB on selected ports" };
    private readonly NumericUpDown poll = new() { Minimum = 1, Maximum = 10, Width = 70 };
    private readonly DisplayEditor panelEditor, aioEditor;
    private readonly SensorSelector sensorSelector;
    private readonly FrameRenderer panelRenderer = new(), aioRenderer = new();
    private string? panelPreviewKey, aioPreviewKey;
    private DisplaySettings? panelPreviewSettings, aioPreviewSettings;
    private readonly Button liveButton;
    private ContextMenuStrip? startupMenu, profileMenu;
    private bool busy, exiting, smokeMode, loadingProfile, startMinimized, closeCompleted;
    private DateTime lastResource = DateTime.UtcNow;
    private readonly DateTime resourceWarmup = DateTime.UtcNow.AddSeconds(5);
    private TimeSpan lastCpu;
    private DateTime nextStatusWrite;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? AcceptanceStatusPath { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool StartMinimized
    {
        get => startMinimized;
        set
        {
            startMinimized = value;
            // Reflect legacy startup arguments in the checkbox, without writing the profile.
            if (settings.MinimizeAfterStartup is null)
            { loadingProfile = true; try { minimizeAfterStartup.Checked = value; } finally { loadingProfile = false; } }
        }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool StartupRequested { get; set; }
    public MainForm(Settings initial, string path, Engine? testEngine = null, string startupWarning = "")
    {
        engine = testEngine ?? new(); settings = initial; configPath = path; this.startupWarning = startupWarning;
        DoubleBuffered = true; Icon = appIcon; tray.Icon = appIcon;
        foreach (var port in initial.Ports) portDesign[port.Port] = port.Copy();
        Text = "ThermalDesk · local hardware control"; BackColor = Background; ForeColor = Color.White;
        Font = new Font("Segoe UI", 10); ClientSize = new Size(1180, 820); MinimumSize = new Size(1000, 720);
        StartPosition = FormStartPosition.CenterScreen;
        var shell = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), RowCount = 5, ColumnCount = 1 };
        shell.RowStyles.Add(new(SizeType.Absolute, 66)); shell.RowStyles.Add(new(SizeType.Absolute, 52));
        shell.RowStyles.Add(new(SizeType.Absolute, 36)); shell.RowStyles.Add(new(SizeType.Percent, 100)); shell.RowStyles.Add(new(SizeType.Absolute, 30));
        Controls.Add(shell);
        shell.Controls.Add(new Label { Text = "THERMALDESK  /  Hardware, without the overhead", ForeColor = Accent, Font = new Font(Font.FontFamily, 21, FontStyle.Bold), AutoSize = true, Padding = new Padding(0, 10, 0, 0) }, 0, 0);
        var tools = new BufferedFlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        tools.Controls.Add(Button("Preview", async () => await Switch(false)));
        liveButton = Button("Start live control", async () => await Switch(true), true); tools.Controls.Add(liveButton);
        tools.Controls.Add(Button("Stop", async () => await Run(async () => await engine.Stop())));
        tools.Controls.Add(Button("Save now", () => { settings = ReadSettings(); SettingsStore.Save(configPath, settings); Info("Your profile is saved. Valid edits already save and apply automatically; Stop / Start is not needed for curves, RGB or widgets."); }));
        tools.Controls.Add(Button("Import / export", ProfileMenu));
        tools.Controls.Add(Button("Start at sign-in", ConfigureStartup));
        if (!StartupManager.Elevated) tools.Controls.Add(Button("Restart as admin", async () => await RestartElevated()));
        shell.Controls.Add(tools, 0, 1);
        session.AutoSize = false; session.Dock = DockStyle.Fill; session.AutoEllipsis = true; session.TextAlign = ContentAlignment.MiddleLeft;
        session.ForeColor = Muted; shell.Controls.Add(session, 0, 2);
        var tabs = new ThemedTabControl { Dock = DockStyle.Fill, Padding = new Point(18, 8) };
        shell.Controls.Add(tabs, 0, 3);
        var dashboard = Page(tabs, "Overview");
        var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 58)); layout.ColumnStyles.Add(new(SizeType.Percent, 42));
        layout.RowStyles.Add(new(SizeType.Absolute, 108)); layout.RowStyles.Add(new(SizeType.Absolute, 170)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        dashboard.Controls.Add(layout);
        var metrics = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        for (int i = 0; i < 3; i++) metrics.ColumnStyles.Add(new(SizeType.Percent, 33.333f));
        metrics.Controls.Add(Metric(cpu), 0, 0); metrics.Controls.Add(Metric(gpu), 1, 0); metrics.Controls.Add(Metric(memory), 2, 0);
        layout.Controls.Add(metrics, 0, 0); layout.SetColumnSpan(metrics, 2);
        panelPreview.Dock = DockStyle.Fill; panelPreview.SizeMode = PictureBoxSizeMode.Zoom; panelPreview.BackColor = Background;
        layout.Controls.Add(Group("3.9-inch panel · 480 × 128", panelPreview), 0, 1);
        aioPreview.Dock = DockStyle.Fill; aioPreview.SizeMode = PictureBoxSizeMode.Zoom; aioPreview.BackColor = Background;
        var aioBox = Group("TOUGHLIQUID AX · 480 × 480", aioPreview);
        layout.Controls.Add(aioBox, 1, 1); layout.SetRowSpan(aioBox, 2);
        ConfigureText(deviceStatus); layout.Controls.Add(Group("Device state / safety", deviceStatus), 0, 2);
        var coolingPage = Page(tabs, "Cooling + RGB");
        var coolingLayout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Padding = new Padding(12) };
        coolingLayout.RowStyles.Add(new(SizeType.Absolute, 65)); coolingLayout.RowStyles.Add(new(SizeType.Absolute, 64)); coolingLayout.RowStyles.Add(new(SizeType.Percent, 100)); coolingLayout.RowStyles.Add(new(SizeType.Absolute, 80));
        coolingPage.Controls.Add(coolingLayout);
        coolingLayout.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(255, 194, 100), Text = "Identify wiring and keep independent thermal protection. Software cannot guarantee protection after a crash, USB loss or before sign-in.\nCurve/RGB changes apply automatically. Stop restores captured duties, not forced full speed. This is not BIOS hand-back." }, 0, 0);
        var flags = new BufferedFlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        cooling.AutoSize = wiring.AutoSize = rgb.AutoSize = true; flags.Controls.AddRange([cooling, rgb, monitorFans, wiring]); coolingLayout.Controls.Add(flags, 0, 1);
        BuildPorts(); coolingLayout.Controls.Add(ports, 0, 2);
        var extras = new BufferedFlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        color.Width = 100; serial.Width = 190;
        extras.Controls.AddRange([new Label { Text = "Default RGB color", AutoSize = true }, color, Button("Choose default…", PickColor), new Label { Text = "Controller serial (optional)", AutoSize = true }, serial]);
        extras.Controls.Add(new Label { Text = "Managed fans:", AutoSize = true });
        extras.Controls.Add(Button("RGB schedule…", () =>
        {
            using var editor = new RgbScheduleForm(ReadSettings());
            if (editor.ShowDialog(this) != DialogResult.OK) return;
            settings.RgbScheduleEnabled = editor.Result.RgbScheduleEnabled;
            settings.RgbScheduleEntries = editor.Result.RgbScheduleEntries;
            QueueSettingsApply();
        }));
        foreach (var preset in Enum.GetValues<FanPreset>()) extras.Controls.Add(Button(preset.ToString(), () => ApplyFanPreset(preset)));
        coolingLayout.Controls.Add(extras, 0, 3);
        var displays = Page(tabs, "Displays");
        var displayLayout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12) };
        displayLayout.ColumnStyles.Add(new(SizeType.Percent, 50)); displayLayout.ColumnStyles.Add(new(SizeType.Percent, 50));
        displayLayout.RowStyles.Add(new(SizeType.Percent, 100)); displayLayout.RowStyles.Add(new(SizeType.Absolute, 68)); displays.Controls.Add(displayLayout);
        panelEditor = new("3.9-inch LCD · 264A:233D", settings.Panel, 480, 128, () => engine.Latest); aioEditor = new("TOUGHLIQUID AX · 264A:2328", settings.Aio, 480, 480, () => engine.Latest);
        displayLayout.Controls.Add(panelEditor, 0, 0); displayLayout.Controls.Add(aioEditor, 1, 0);
        displayLayout.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Muted, Text = "Edit widgets: pages, gauges, graphs and online JPEG/PNG/MJPEG. Page 0 stays visible; other pages rotate.\nOnline sources fetch only in Live (up to four). Screen mirroring may show private information. No web server or cloud account." }, 0, 1); displayLayout.SetColumnSpan(displayLayout.GetControlFromPosition(0, 1)!, 2);
        var sensorsPage = Page(tabs, "Sensors");
        sensorSelector = new SensorSelector(initial.PreferredGpuId, () => engine.Latest, initial.AllowMonitoringOnly); sensorsPage.Controls.Add(sensorSelector); sensorSelector.SettingsChanged += QueueSettingsApply;
        var preferences = Page(tabs, "Settings");
        var preferenceLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(24) };
        preferences.Controls.Add(preferenceLayout);
        preferenceLayout.Controls.Add(new Label { Text = "Startup, profile and tray", AutoSize = true, ForeColor = Accent, Font = new Font(Font.FontFamily, 14, FontStyle.Bold), Margin = new Padding(0, 0, 0, 18) });
        resumeLastProfile.Checked = initial.ResumeLastProfileOnLaunch ?? (initial.LastSessionWasLive || initial.StartLiveAtSignIn);
        preferenceLayout.Controls.Add(resumeLastProfile);
        preferenceLayout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Muted, Text = "Load and activate your saved devices when opening the app. Only previously configured outputs are used; this does not add Windows startup. Hold Shift or use --preview to open without hardware access. Recovery and conflicting controller apps still block automatic control.", Margin = new Padding(24, 4, 0, 22) });
        hints.SetToolTip(resumeLastProfile, "Applies on the next launch, not immediately. OFF keeps manual launches in Preview; Windows sign-in mode is configured separately. Your profile is always loaded and saved automatically.");
        TrackSettingsChanges(resumeLastProfile);
        minimizeAfterStartup.Checked = initial.MinimizeAfterStartup ?? false; minimizeWhenClosed.Checked = initial.MinimizeWhenClosed;
        preferenceLayout.Controls.Add(minimizeAfterStartup);
        preferenceLayout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Muted, Text = "Hide the window in the system tray after a successful launch. This does not enable Live or Windows sign-in startup. Hold Shift or launch with --show-window to open visibly.", Margin = new Padding(24, 4, 0, 22) });
        preferenceLayout.Controls.Add(minimizeWhenClosed);
        preferenceLayout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Muted, Text = "The window's X / Alt+F4 hides ThermalDesk instead of exiting. Sensors, fans, RGB and LCDs keep running in the same session. To actually quit, right-click the ThermalDesk icon by the clock and choose Exit. Windows shutdown is not treated as hiding.", Margin = new Padding(24, 4, 0, 22) });
        preferenceLayout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Muted, Text = "Changes save automatically. Double-click the tray icon, or choose Open, to restore the window. Configure automatic Windows launch separately with Start at sign-in." });
        technicalDetails.Checked = initial.ShowTechnicalDetails; preferenceLayout.Controls.Add(technicalDetails); TrackSettingsChanges(technicalDetails);
        hints.SetToolTip(technicalDetails, "Show thread and handle counts in the footer. Off by default: the normal view shows just application CPU and memory and skips these extra process queries.");
        hints.SetToolTip(minimizeAfterStartup, "Start hidden by the Windows clock. Does not automatically enable hardware. Explicit OFF also overrides --minimized in older sign-in tasks.");
        hints.SetToolTip(minimizeWhenClosed, "X and Alt+F4 only hide the window; hardware control continues. Exit in the tray menu always shuts down the app normally.");
        TrackSettingsChanges(minimizeAfterStartup); TrackSettingsChanges(minimizeWhenClosed);
        preferenceLayout.Controls.Add(new Label { Text = "Process activity (optional)", AutoSize = true, ForeColor = Accent, Font = new Font(Font.FontFamily, 14, FontStyle.Bold), Margin = new Padding(0, 26, 0, 18) });
        processMonitoring.Checked = initial.ProcessMonitorEnabled; processInterval.Value = initial.ProcessSampleSeconds;
        preferenceLayout.Controls.Add(processMonitoring);
        var processSampling = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        processSampling.Controls.Add(new Label { AutoSize = true, Text = "Sample interval (seconds)", Margin = new Padding(0, 5, 10, 0) }); processSampling.Controls.Add(processInterval); preferenceLayout.Controls.Add(processSampling);
        preferenceLayout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Muted, Text = "Read-only: process name, PID and CPU/GPU utilization. The Process activity tab keeps only 20 recent changes in memory. CPU is share of total PC time; GPU is the busiest engine across all adapters, not VRAM usage. Protected/exited processes may be unavailable. Nothing is terminated or reprioritized. Automatic process-log widgets also request sampling while enabled.", Margin = new Padding(24, 6, 0, 14) });
        preferenceLayout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Muted, Text = "LCD: Edit widgets → Source → Top CPU process / Top GPU process. These are text widgets; names may be visible to people near the PC. No real process sampling occurs in Preview." });
        hints.SetToolTip(processMonitoring, "Optional Windows process-time and GPU performance-counter sampling. Disabled by default; independent from cooling. Enable then use Live for actual values.");
        hints.SetToolTip(processInterval, "5–60 seconds. Longer intervals reduce process enumeration and counter work. This never changes the temperature sampling interval used for cooling.");
        TrackSettingsChanges(processMonitoring); TrackSettingsChanges(processInterval);
        logInterval.Value = initial.LogSampleSeconds;
        var logSampling = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        logSampling.Controls.Add(new Label { AutoSize = true, Text = "Log widget interval (seconds)", Margin = new Padding(0, 5, 10, 0) }); logSampling.Controls.Add(logInterval); preferenceLayout.Controls.Add(logSampling);
        preferenceLayout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Muted, Text = "Displays → Edit widgets → Add widget → Local text file / Auto log. Reads only in Live: last 32 KiB, up to 10 short lines; unchanged files are cached. Automatic discovery checks at most 6 related folders (384 entries), once/minute per winning process, without recursion or command-line access. Some apps have no file log. See Device state for the selected candidate path. Common credentials are redacted, but do not put sensitive logs on a visible LCD.", Margin = new Padding(24, 6, 0, 14) });
        hints.SetToolTip(logInterval, "5–60 seconds; independent from cooling. No file-log reads until a log widget is enabled on an active display in Live.");
        TrackSettingsChanges(logInterval);
        preferenceLayout.Controls.Add(new Label { Text = "Whole-PC power estimate", AutoSize = true, ForeColor = Accent, Font = new Font(Font.FontFamily, 14, FontStyle.Bold), Margin = new Padding(0, 26, 0, 18) });
        powerExtra.Value = (decimal)initial.PowerEstimateExtraWatts; powerEfficiency.Value = (decimal)initial.PowerEstimateEfficiencyPercent; powerGpu.Checked = initial.PowerEstimateIncludesGpu;
        var powerSettings = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        powerSettings.Controls.Add(new Label { AutoSize = true, Text = "Other components (W)", Margin = new Padding(0, 5, 8, 0) }); powerSettings.Controls.Add(powerExtra);
        powerSettings.Controls.Add(new Label { AutoSize = true, Text = "PSU efficiency (%)", Margin = new Padding(18, 5, 8, 0) }); powerSettings.Controls.Add(powerEfficiency);
        preferenceLayout.Controls.Add(powerSettings); preferenceLayout.Controls.Add(powerGpu);
        preferenceLayout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Muted, Text = "Estimate = (reported CPU package + selected GPU + other components) / PSU efficiency. Defaults 40 W / 90% are uncalibrated assumptions, not measurements. Excludes the monitor, external peripherals and other GPUs. If required sensors are missing, total stays unavailable. Adjust against a wall meter. Hourly averages use sampled time coverage, not zero-filled gaps. Changing GPU/calibration resets history. Graphs keep only one hour in RAM, sampled at most every five seconds.", Margin = new Padding(0, 6, 0, 14) });
        hints.SetToolTip(powerExtra, "Motherboard/RAM/storage/fans allowance. A wall meter is needed for calibration; this does not control any hardware.");
        hints.SetToolTip(powerEfficiency, "Approximate efficiency at the current load, not the PSU's nameplate rating. 90% is only an initial assumption.");
        TrackSettingsChanges(powerExtra); TrackSettingsChanges(powerEfficiency); TrackSettingsChanges(powerGpu);
        var activityPage = Page(tabs, "Process activity");
        var activityLayout = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18) }; activityPage.Controls.Add(activityLayout);
        ConfigureText(processActivityLog); activityLayout.Controls.Add(processActivityLog); activityLayout.Controls.Add(processActivityStatus);
        var logsPage = Page(tabs, "Log + diagnostics"); ConfigureText(log); logsPage.Controls.Add(log);
        if (startupWarning.Length > 0) log.Text = "STARTUP RECOVERY: " + startupWarning + Environment.NewLine;
        var diag = Button("Export diagnostics", ExportDiagnostics); diag.Dock = DockStyle.Bottom; logsPage.Controls.Add(diag); diag.BringToFront();
        var about = Page(tabs, "About");
        var aboutText = new TextBox(); ConfigureText(aboutText); aboutText.Text = $"ThermalDesk {typeof(MainForm).Assembly.GetName().Version?.ToString(3)} — native telemetry + experimental Thermaltake control\r\n\r\nTelemetry: Intel/AMD CPUs; Intel/AMD/NVIDIA GPUs where supported by hardware/driver access. Missing readings stay unavailable. Sensors shows the exact device and source. Shared RAM is not dedicated VRAM.\r\n\r\nUSB devices: 264A:233D panel, 264A:2328 AX LCD, 264A:232B five-port fan controller. Unknown descriptors/firmware are rejected. This is not universal motherboard fan/RGB control.\r\n\r\nYour saved profile loads automatically. Previously armed profiles can resume Live; Settings controls this choice. Enable the correct USB output before Start live control, or opt into monitoring-only in Sensors. CPU sensors can require administrator access and supported PawnIO.\r\n\r\nAutomatic Live is opt-in. Hold Shift or use --preview to bypass it. Vendor conflicts block USB control, not monitoring-only. Apps are never killed. This is not a before-login cooling service.\r\n\r\nLog widgets: compact AI throughput or Gaming FPS/frame time/network RTT, only when the selected log reports them. PresentMon presentation cadence and PC latency are not network ping. No game injection or automatic tracing.\r\n\r\nDuty % is a controller request, not watts or RPM. PWM/DC electrical switching is not exposed. BIOS pump control remains unchanged. A crash, USB loss or frozen process cannot be made safe by an exit handler. Keep independent firmware/BIOS thermal protection.\r\n\r\nPhysical acceptance is required for LCD images, RGB colors, wiring and cooling. Automated tests are not certification. No PSU/RAM RGB/cloud/firmware updates. No network server. GPL-3.0-or-later; see LICENSE and THIRD-PARTY-NOTICES.txt."; about.Controls.Add(aboutText);
        var bottom = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        bottom.ColumnStyles.Add(new(SizeType.Absolute, 145)); bottom.ColumnStyles.Add(new(SizeType.Absolute, 80)); bottom.ColumnStyles.Add(new(SizeType.Percent, 100));
        bottom.Controls.Add(new Label { Text = "Sensor interval (s)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        poll.Value = initial.PollSeconds; bottom.Controls.Add(poll, 1, 0);
        resources.AutoSize = false; resources.Dock = DockStyle.Fill; resources.AutoEllipsis = true; resources.TextAlign = ContentAlignment.MiddleLeft;
        resources.ForeColor = Muted; bottom.Controls.Add(resources, 2, 0); shell.Controls.Add(bottom, 0, 4);
        cooling.Checked = initial.CoolingEnabled; wiring.Checked = initial.CoolingWiringConfirmed; rgb.Checked = initial.RgbEnabled; monitorFans.Checked = initial.MonitorFanController; color.Text = initial.RgbColor; serial.Text = initial.ControllerSerial;
        TrackSettingsChanges(cooling); TrackSettingsChanges(wiring); TrackSettingsChanges(rgb); TrackSettingsChanges(monitorFans);
        TrackSettingsChanges(color); TrackSettingsChanges(serial); TrackSettingsChanges(poll); TrackSettingsChanges(panelEditor); TrackSettingsChanges(aioEditor);
        // Feedback columns must never trigger saving/reapplying settings every poll.
        ports.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex is < 1 or > 8) return;
            if (e.ColumnIndex == 6) TrackRgbSelection(ports.Rows[e.RowIndex]);
            QueueSettingsApply();
        };
        ports.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // Combo/checkbox changes should not wait for focus to leave the row.
            // Text editing still commits normally, so half-typed curves stay local.
            if (ports.IsCurrentCellDirty && ports.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                ports.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        panelEditor.SettingsChanged += QueueSettingsApply; aioEditor.SettingsChanged += QueueSettingsApply;
        settingsDelay.Tick += (_, _) => ApplyPendingSettings();
        tray.ContextMenuStrip = new ContextMenuStrip(); tray.ContextMenuStrip.Items.Add("Open", null, (_, _) => RestoreFromTray());
        tray.ContextMenuStrip.Items.Add("Stop hardware control", null, async (_, _) => await Run(async () => await engine.Stop()));
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => RequestExit()); tray.DoubleClick += (_, _) => RestoreFromTray();
        tabs.SelectedIndexChanged += (_, _) => { if (Visible && !busy) RefreshData(); };
        refresh.Tick += (_, _) => { if (Visible && !busy) RefreshData(); WriteAcceptanceStatus(); };
        Shown += (_, _) =>
        {
            if (smokeMode) return;
            string? blocked = StartInitialSession(); refresh.Start(); tray.Visible = true;
            if (blocked is null) { if (StartMinimized) HideToTray(); }
            else { Show(); Error(new InvalidOperationException("Automatic control was blocked safely. Preview is available; no conflicting app was stopped. " + blocked)); }
        };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && tray.Visible) HideToTray(); };
        FormClosing += ClosingAsync;
        ApplyColors(this);
        hints.SetToolTip(cooling, "Controls only explicitly managed USB ports, not motherboard headers or the BIOS pump. Changing ownership requires Stop and confirmation.");
        hints.SetToolTip(wiring, "Confirm actual fan/pump wiring and independent hardware thermal protection before enabling cooling.");
        hints.SetToolTip(rgb, "Global RGB master. Live control is also required. Disable to release lighting control; choose Turn off in a port editor to keep that port dark.");
        hints.SetToolTip(monitorFans, "Read RPM and reported duty without changing fan speeds. Requires Live and exclusive access to the TT controller.");
        hints.SetToolTip(color, "Default six-digit RGB hex color, for example 00FFAA. Per-port colors or multi-color effects can override it.");
        hints.SetToolTip(serial, "Leave empty for one supported controller. With several controllers, enter the exact serial from Device state.");
        hints.SetToolTip(poll, "Seconds between sensor samples. LCD frame rate is separate. A shorter interval can increase CPU and hardware polling work.");
        foreach (Control tool in tools.Controls)
            hints.SetToolTip(tool, tool.Text switch
            {
                "Preview" => "Stops live control and renders simulated readings without USB access.",
                "Start live control" => "Starts the enabled devices after confirmation. Later valid edits apply automatically; do not repeatedly stop/start for color or curve changes.",
                "Stop" => "Releases managed devices and restores captured fan duties, not forced full speed. This is not a BIOS hand-back.",
                "Save now" => "Optional immediate save. Valid edits are already saved automatically after a short pause; you do not need Stop / Start to apply them.",
                "Import / export" => "Load a profile into this window immediately, export a JSON copy, or open the profile folder. Import keeps a dated backup and does not change Windows startup.",
                "Start at sign-in" => "Choose disabled, Preview, or elevated saved-live startup. Existing task settings are not silently enabled by installation.",
                _ => tool.Text
            });
    }
    private static TabPage Page(TabControl tabs, string title) { var p = new TabPage(title) { BackColor = Background, ForeColor = Color.White }; tabs.TabPages.Add(p); return p; }
    private static GroupBox Group(string title, Control control) { var b = new GroupBox { Text = title, Dock = DockStyle.Fill, ForeColor = Muted, Padding = new Padding(10), Margin = new Padding(5) }; b.Controls.Add(control); return b; }
    private static Panel Metric(Label l) { var p = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(14), Margin = new Padding(5) }; l.Dock = DockStyle.Fill; l.Font = new Font("Segoe UI", 15, FontStyle.Bold); p.Controls.Add(l); return p; }
    private Button Button(string title, Action click, bool accent = false)
    {
        var b = new Button { Text = title, AutoSize = true, Height = 35, FlatStyle = FlatStyle.Flat, Margin = new Padding(3), BackColor = accent ? Accent : Surface, ForeColor = accent ? Background : Color.White, Padding = new Padding(6, 3, 6, 3) };
        b.FlatAppearance.BorderColor = Color.FromArgb(52, 75, 92);
        b.Click += (_, _) => { try { click(); } catch (Exception e) { Error(e); } }; return b;
    }
    private static void ConfigureText(TextBox t) { t.Multiline = t.ReadOnly = true; t.ScrollBars = ScrollBars.Vertical; t.Dock = DockStyle.Fill; t.BorderStyle = BorderStyle.None; t.Font = new Font("Consolas", 10); }
    private static void ApplyColors(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is TextBox or ComboBox or NumericUpDown) { c.BackColor = Surface; c.ForeColor = Color.White; }
            else if (c is Label && c.ForeColor == SystemColors.ControlText) c.ForeColor = Color.White;
            ApplyColors(c);
        }
    }
    private void BuildPorts()
    {
        ports.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == 0 && e.RowIndex >= 0 && e.Value is int number && portDesign.TryGetValue(number, out var p) && p.Name.Length > 0)
            { e.Value = $"{number} · {p.Name}"; e.FormattingApplied = true; }
        };
        ports.Dock = DockStyle.Fill; ports.AllowUserToAddRows = ports.AllowUserToDeleteRows = false; ports.RowHeadersVisible = false; ports.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        ports.BackgroundColor = Surface; ports.BorderStyle = BorderStyle.None; ports.DefaultCellStyle.BackColor = Surface; ports.DefaultCellStyle.ForeColor = Color.White;
        ports.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        ports.DefaultCellStyle.SelectionBackColor = Color.FromArgb(36, 81, 93); ports.EnableHeadersVisualStyles = false;
        ports.ColumnHeadersDefaultCellStyle.BackColor = Background; ports.ColumnHeadersDefaultCellStyle.ForeColor = Muted; ports.RowTemplate.Height = 34;
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "Port", ReadOnly = true, FillWeight = 85 });
        foreach (var pair in new[] { ("Role", Enum.GetNames<PortRole>()), ("Mode", Enum.GetNames<ControlMode>()), ("Source", Enum.GetNames<TemperatureSource>()) })
            ports.Columns.Add(new DataGridViewComboBoxColumn { Name = pair.Item1, DataSource = pair.Item2, FlatStyle = FlatStyle.Flat, FillWeight = pair.Item1 == "Source" ? 100 : 75 });
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "Minimum", HeaderText = "Min %", FillWeight = 42 });
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "Fixed", HeaderText = "Fixed %", FillWeight = 42 });
        ports.Columns.Add(new DataGridViewCheckBoxColumn { Name = "RGB", FillWeight = 36 });
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "LEDs", FillWeight = 36 });
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "Curve", HeaderText = "Curve · °C:% separated by ;", FillWeight = 210 });
        ports.Columns.Add(new DataGridViewButtonColumn { Name = "Editor", HeaderText = "Curve / RGB", Text = "Edit…", UseColumnTextForButtonValue = true, FillWeight = 60 });
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "Requested", HeaderText = "Request → target %", ReadOnly = true, FillWeight = 85, ToolTipText = "Current software command → final curve target. Reductions are smoothed; percentages are not RPM or watts." });
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reported", HeaderText = "Reported %", ReadOnly = true, FillWeight = 55, ToolTipText = "Duty returned by the controller, not an independent measurement of electrical power." });
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "RPM", HeaderText = "RPM", ReadOnly = true, FillWeight = 50, ToolTipText = "Tachometer feedback in revolutions per minute; unavailable after Stop or a controller fault." });
        var help = new[]
        {
            "Physical controller port, not a motherboard fan header. Edit to add a friendly name.",
            "Fan or pump wiring. Unused ports are never speed-controlled. Changing wiring requires rearming.",
            "Preserve: no speed writes. Fixed: constant duty. Curve: temperature-dependent duty.",
            "Temperature used for the curve, not fan speed. Missing required data releases software control without forced full speed; independent hardware protection is essential.",
            "Minimum requested duty for this port. It can prevent a Silent preset from reaching a lower speed.",
            "Constant duty in Fixed mode only. Percent is not RPM or electrical power.",
            "Checked: selected lighting effect. Unchecking an enabled port explicitly requests black while the global RGB master is on. Edit offers Leave unchanged.",
            "Number of addressable LED positions on this port, NOT number of fans. Match the connected product; ThermalDesk cannot detect it automatically.",
            "Temperature:duty points, separated by semicolons. For a visual editor and Silent/Custom/Full presets, click Edit.",
            "Visual curve editor, simple presets, RGB colors/effects and detailed hover hints. Changes apply when accepted."
        };
        for (int i = 0; i < help.Length; i++) ports.Columns[i].ToolTipText = help[i];
        ports.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.ColumnIndex < 0) return;
            e.ToolTipText = ports.Columns[e.ColumnIndex].ToolTipText;
            if (e.ColumnIndex == 6 && e.RowIndex >= 0 && portDesign.TryGetValue(Convert.ToInt32(ports.Rows[e.RowIndex].Cells[0].Value), out var port))
                e.ToolTipText = $"Current selection: {Lighting.StateLabel(port)}.\n" + e.ToolTipText;
        };
        foreach (var p in settings.Ports) ports.Rows.Add(p.Port, p.Role.ToString(), p.Mode.ToString(), p.Source.ToString(), p.MinimumDuty, p.FixedDuty, p.Rgb, p.LedCount, string.Join(';', p.Curve.Select(x => $"{x.Temperature.ToString(CultureInfo.InvariantCulture)}:{x.Duty.ToString(CultureInfo.InvariantCulture)}")));
        ports.DataError += (_, e) => { e.ThrowException = false; };
        ports.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 9) return;
            try
            {
                ports.EndEdit(); var row = ports.Rows[e.RowIndex]; using var editor = new PortEditorForm(ReadPort(row), () => engine.Latest, color.Text, rgb.Checked);
                if (editor.ShowDialog(this) != DialogResult.OK) return;
                AcceptPortEditorResult(row, editor.Result);
            }
            catch (Exception error) { Error(error); }
        };
    }
    private void AcceptPortEditorResult(DataGridViewRow row, PortSettings value)
    {
        var p = value.Copy(); portDesign[p.Port] = p;
        row.SetValues(p.Port, p.Role.ToString(), p.Mode.ToString(), p.Source.ToString(), p.MinimumDuty, p.FixedDuty, p.Rgb, p.LedCount,
            string.Join(';', p.Curve.Select(x => $"{x.Temperature.ToString(CultureInfo.InvariantCulture)}:{x.Duty.ToString(CultureInfo.InvariantCulture)}")));
        // RGB effects, colors and names are not represented in editable grid cells.
        // Accepting the dialog must enqueue an update even if every grid value is unchanged.
        QueueSettingsApply();
    }
    private PortSettings ReadPort(DataGridViewRow row)
    {
        static string Cell(DataGridViewRow r, int i) => Convert.ToString(r.Cells[i].Value, CultureInfo.InvariantCulture) ?? throw new ArgumentException("Missing port value.");
        int port = Convert.ToInt32(row.Cells[0].Value); var p = portDesign[port].Copy();
        p.Role = Enum.Parse<PortRole>(Cell(row, 1)); p.Mode = Enum.Parse<ControlMode>(Cell(row, 2)); p.Source = Enum.Parse<TemperatureSource>(Cell(row, 3)); p.MinimumDuty = Convert.ToInt32(row.Cells[4].Value); p.FixedDuty = Convert.ToInt32(row.Cells[5].Value); p.Rgb = Convert.ToBoolean(row.Cells[6].Value); p.LedCount = Convert.ToInt32(row.Cells[7].Value);
        p.Curve = Cell(row, 8).Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => { var pair = x.Split(':'); if (pair.Length != 2) throw new ArgumentException("Curve format: 30:40;50:50;65:70;75:90;80:100"); return new CurvePoint(double.Parse(pair[0], CultureInfo.InvariantCulture), double.Parse(pair[1], CultureInfo.InvariantCulture)); }).ToList();
        if (!p.Curve.SequenceEqual(portDesign[port].Curve)) p.CustomCurveBackup.Clear();
        return p;
    }
    private void TrackRgbSelection(DataGridViewRow row)
    {
        int number = Convert.ToInt32(row.Cells[0].Value);
        if (!portDesign.TryGetValue(number, out var port)) return;
        bool selected = Convert.ToBoolean(row.Cells[6].Value);
        if (selected == port.Rgb) return; // Editor already supplied explicit OFF/unchanged intent.
        port.RgbOffRequested = !selected;
        port.Rgb = selected;
    }
    private Settings ReadSettings()
    {
        ports.EndEdit();
        var s = new Settings { PollSeconds = (int)poll.Value, PreferredGpuId = sensorSelector.Read(), CoolingEnabled = cooling.Checked, CoolingWiringConfirmed = wiring.Checked, RgbEnabled = rgb.Checked, MonitorFanController = monitorFans.Checked, StartLiveAtSignIn = settings.StartLiveAtSignIn, ResumeLastProfileOnLaunch = resumeLastProfile.Checked, ShowTechnicalDetails = technicalDetails.Checked, MinimizeAfterStartup = minimizeAfterStartup.Checked, MinimizeWhenClosed = minimizeWhenClosed.Checked, RgbColor = color.Text.Trim().TrimStart('#'), ControllerSerial = serial.Text.Trim(), Panel = panelEditor.Read(), Aio = aioEditor.Read() };
        s.ProcessMonitorEnabled = processMonitoring.Checked; s.ProcessSampleSeconds = (int)processInterval.Value; s.LogSampleSeconds = (int)logInterval.Value;
        s.AllowMonitoringOnly = sensorSelector.AllowMonitoringOnly;
        s.LastSessionWasLive = settings.LastSessionWasLive;
        s.ResumeLastProfileOnLaunch = resumeLastProfile.Checked ? true : settings.ResumeLastProfileOnLaunch.HasValue ? false : null;
        s.RgbScheduleEnabled = settings.RgbScheduleEnabled;
        s.RgbScheduleEntries = settings.RgbScheduleEntries.Select(e => new RgbScheduleEntry { Time = e.Time, Days = e.Days, BrightnessPercent = e.BrightnessPercent, Color = e.Color }).ToList();
        s.PowerEstimateExtraWatts = (double)powerExtra.Value; s.PowerEstimateEfficiencyPercent = (double)powerEfficiency.Value; s.PowerEstimateIncludesGpu = powerGpu.Checked;
        s.Ports = ports.Rows.Cast<DataGridViewRow>().Select(ReadPort).ToList(); s.Validate(); return s;
    }
    private void ApplyFanPreset(FanPreset preset)
    {
        ports.EndEdit();
        foreach (DataGridViewRow row in ports.Rows)
        {
            var p = ReadPort(row);
            if (p.Role != PortRole.Fan || p.Mode == ControlMode.Preserve) continue;
            FanPresets.Apply(p, preset); portDesign[p.Port] = p.Copy();
            row.SetValues(p.Port, p.Role.ToString(), p.Mode.ToString(), p.Source.ToString(), p.MinimumDuty, p.FixedDuty, p.Rgb, p.LedCount,
                string.Join(';', p.Curve.Select(x => $"{x.Temperature.ToString(CultureInfo.InvariantCulture)}:{x.Duty.ToString(CultureInfo.InvariantCulture)}")));
        }
        QueueSettingsApply();
    }
    private async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true;
        try { await action(); } catch (Exception e) { Error(e); }
        finally { busy = false; RefreshData(); if (exiting && !closeCompleted && !IsDisposed) BeginInvoke((Action)Close); }
    }
    private async Task Switch(bool live) => await Run(async () =>
    {
        var next = ReadSettings();
        if (live)
        {
            next.ValidateLiveSelection();
            if (next.CoolingEnabled && !StartupManager.Elevated)
                throw new InvalidOperationException("CPU sensor access on this PC requires administrator rights. Click Restart as admin first. No cooling session was started.");
            bool updating = engine.Running && engine.Live;
            string warning = updating ? "Apply curve, RGB and widget settings without restarting the controller? Device/wiring changes require Stop first."
                : !next.HasUsbSelection ? "Start read-only CPU/GPU/process monitoring? No USB output is selected, no fan/RGB command will be sent and other controller applications do not need to close."
                : "Start live control of the enabled devices? Close TT RGB Plus, OpenRGB, Fan Control and SignalRGB first. No application will be closed automatically.";
            if (next.CoolingEnabled) warning += "\n\nCooling: you must have verified wiring and independent thermal protection. USB disconnection or a crashed/frozen process cannot guarantee a safe fan speed. Do not use experimental cooling unattended.";
            if (next.Panel.Content == DisplayContent.Screen || next.Aio.Content == DisplayContent.Screen) warning += "\n\nScreen mirroring can display private information on the physical LCD.";
            if (MessageBox.Show(warning, "Confirm live hardware access", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            if (updating) { engine.ApplyLiveSettings(next); settings = next; return; }
        }
        await engine.Stop(); settings = next; engine.Start(next, live);
        if (live)
        {
            // First successful, explicitly confirmed Live start opts into resume.
            // An existing explicit opt-out is never replaced.
            StartupManager.RememberSuccessfulLive(next); resumeLastProfile.Checked = next.ResumeLastProfileOnLaunch == true;
            settings = next; SettingsStore.Save(configPath, next);
        }
    });
    private void TrackSettingsChanges(Control control)
    {
        switch (control)
        {
            case CheckBox check: check.CheckedChanged += (_, _) => QueueSettingsApply(); break;
            case ComboBox combo: combo.SelectedIndexChanged += (_, _) => QueueSettingsApply(); break;
            case NumericUpDown number: number.ValueChanged += (_, _) => QueueSettingsApply(); break;
            case TextBox text when !text.ReadOnly: text.TextChanged += (_, _) => QueueSettingsApply(); break;
        }
        foreach (Control child in control.Controls) TrackSettingsChanges(child);
    }
    private void QueueSettingsApply()
    { if (smokeMode || exiting || loadingProfile) return; settingsDelay.Stop(); settingsDelay.Start(); }
    private void ApplyPendingSettings()
    {
        settingsDelay.Stop();
        if (busy || exiting) { if (!exiting) settingsDelay.Start(); return; }
        try
        {
            var next = ReadSettings();
            if (engine.Running && engine.Live) engine.ApplyLiveSettings(next);
            settings = next; SettingsStore.Save(configPath, next);
            applyHint = engine.Live ? "Changes applied automatically · no controller restart" : "Profile saved · preview updated";
            RefreshData();
        }
        catch (Exception error) { applyHint = "Pending · " + error.Message; RefreshData(); }
    }
    private void RefreshData()
    {
        var t = engine.Latest;
        if (processActivityStatus.Visible)
            SetText(processActivityStatus, "CPU: " + (t.TopCpuProcess?.Summary ?? "—") + "\nGPU: " + (t.TopGpuProcess?.Summary ?? "—") + (t.TopGpuProcess is { } top ? " · " + top.Engine : "") +
                "\n" + t.ProcessMonitorStatus + "\n" + (!engine.Live ? "Preview: no process sampling. Enable in Settings, then use Live." : "Sample: " + (t.ProcessSampleTime == DateTimeOffset.MinValue ? "waiting" : t.ProcessSampleTime.LocalDateTime.ToString("HH:mm:ss"))));
        if (processActivityLog.Visible) UpdateReadOnlyText(processActivityLog, string.Join(Environment.NewLine, engine.ProcessLogs));
        static string Temp(double? x) => x.HasValue ? $"{x:0.0} °C" : "Unavailable";
        static string Load(double? x) => x.HasValue && double.IsFinite(x.Value) && x is >= 0 and <= 100 ? $"{x:0}% load" : "Load unavailable";
        SetText(cpu, engine.Live && !t.CpuTemperature.HasValue && !StartupManager.Elevated ? "CPU\nUnavailable · restart as admin" : $"CPU\n{Temp(t.CpuTemperature)}   ·   {Load(t.CpuLoad)}");
        SetText(gpu, $"GPU\n{Temp(t.GpuTemperature)}   ·   {Load(t.GpuLoad)}");
        SetText(memory, $"MEMORY\n{(t.MemoryUsedPercent.HasValue ? $"{t.MemoryUsedPercent:0}% physical used" : "Unavailable")}");
        if (sensorSelector.Visible) sensorSelector.UpdateTelemetry();
        string sessionText = engine.Cancelled ? "CONTROL PAUSED · see Log + diagnostics, then Stop before reconnecting" : engine.Running ? (engine.Live ? "RUNNING · saved devices active · valid edits apply automatically" : "PREVIEW · sample values only · devices are not controlled") : "STOPPED · your saved profile is loaded; devices are not controlled";
        if (engine.Running && engine.Live && settings.AllowMonitoringOnly && !settings.HasUsbSelection) sessionText = "MONITORING ONLY · real sensors/processes · no USB outputs selected";
        if (engine.Running && !engine.Live && settings.StartLiveAtSignIn && !settings.HasLiveSelection)
            sessionText = "PREVIEW · no live output selected · automatic Live is paused";
        session.ForeColor = engine.Live ? Color.FromArgb(255, 194, 100) : Accent;
        SetText(liveButton, engine.Running && engine.Live ? "Running · automatic" : settings.AllowMonitoringOnly && !settings.HasUsbSelection ? "Start monitoring" : "Start live control");
        liveButton.Enabled = !engine.Running || !engine.Live;
        if (engine.ControllerFault) sessionText = "CONTROLLER BLOCKED / FAULT · see log · verify vendor control";
        if (applyHint.Length > 0) sessionText += " · " + applyHint;
        if (startupWarning.Length > 0) { sessionText += " · PROFILE RECOVERY · review Log before enabling devices"; session.ForeColor = Color.Orange; }
        panelEditor.UpdateConnectionStatus(engine.Status.TryGetValue("480×128 panel", out var panelStatus) ? panelStatus : "Offline editor · hardware is opened only in a live session");
        aioEditor.UpdateConnectionStatus(engine.Status.TryGetValue("TOUGHLIQUID AX", out var aioStatus) ? aioStatus : "Offline editor · hardware is opened only in a live session");
        if (deviceStatus.Visible) UpdateReadOnlyText(deviceStatus, string.Join(Environment.NewLine + Environment.NewLine, engine.Status.OrderBy(x => x.Key).Select(x => x.Key + "\r\n" + x.Value)) + "\r\n\r\n" + string.Join("\r\n", engine.Fans.Values.OrderBy(x => x.Port).Select(x => $"Port {x.Port}: {x.Rpm} RPM / reported {x.Duty}%")));
        if (ports.Visible) foreach (DataGridViewRow row in ports.Rows)
        {
            int port = Convert.ToInt32(row.Cells[0].Value);
            string requested = engine.FanCommands.TryGetValue(port, out var command) ? $"{command.RequestedDuty} → {command.TargetDuty}" : "—";
            bool hasReading = engine.Fans.TryGetValue(port, out var reading);
            void Update(int column, string value) { if (!Equals(row.Cells[column].Value, value)) row.Cells[column].Value = value; }
            Update(10, requested); Update(11, hasReading ? reading!.Duty.ToString(CultureInfo.InvariantCulture) : "—"); Update(12, hasReading ? reading!.Rpm.ToString(CultureInfo.InvariantCulture) : "—");
        }
        if (log.Visible) UpdateReadOnlyText(log, (startupWarning.Length == 0 ? "" : "STARTUP RECOVERY: " + startupWarning + Environment.NewLine + Environment.NewLine) + string.Join(Environment.NewLine, engine.Logs));
        void Preview(PictureBox view, FrameRenderer renderer, DisplaySettings display, int height, ref string? oldKey, ref DisplaySettings? oldSettings)
        {
            if (!view.Visible) return;
            var now = t with { Time = DateTimeOffset.Now }; string? key = FrameRenderer.VisualKey(now, display, engine.OnlineMedia);
            if (view.Image is not null && key is not null && key == oldKey && ReferenceEquals(oldSettings, display)) return;
            var next = renderer.Render(now, display, 480, height, engine.OnlineMedia); var old = view.Image; view.Image = next; old?.Dispose(); oldKey = key; oldSettings = display;
        }
        try { Preview(panelPreview, panelRenderer, settings.Panel, 128, ref panelPreviewKey, ref panelPreviewSettings); } catch (Exception e) { sessionText += " · panel preview: " + e.Message; }
        try { Preview(aioPreview, aioRenderer, settings.Aio, 480, ref aioPreviewKey, ref aioPreviewSettings); } catch (Exception e) { sessionText += " · AIO preview: " + e.Message; }
        SetText(session, sessionText);
        if ((DateTime.UtcNow - lastResource).TotalSeconds < 5) return;
        using var p = Process.GetCurrentProcess();
        var now = DateTime.UtcNow; double elapsed = (now - lastResource).TotalSeconds;
        double percent = elapsed > 0 ? (p.TotalProcessorTime - lastCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100 : 0;
        string detail = settings.ShowTechnicalDetails ? $" · {p.Threads.Count} threads · {p.HandleCount} handles" : "";
        SetText(resources, $"    {(StartupManager.Elevated ? "Administrator" : "Limited user")} · ThermalDesk: {(now < resourceWarmup ? "warming up" : $"{percent:0.00}% CPU")} · {p.WorkingSet64 / 1048576d:0} MiB{detail}");
        lastCpu = p.TotalProcessorTime; lastResource = now;
    }
    private static void SetText(Control control, string value) { if (control.Text != value) control.Text = value; }
    internal string? StartInitialSession()
    {
        try { engine.Start(settings, StartupRequested); return null; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            // Known validation/conflict failures happen before workers/hardware
            // are started. Fall back only while no session exists.
            if (!engine.Running) engine.Start(settings, false);
            applyHint = "Automatic control blocked; open Log + diagnostics / Device state";
            return error.Message;
        }
    }
    private void PickColor()
    {
        using var d = new ColorDialog { FullOpen = true };
        if (color.Text.Trim().TrimStart('#').Length == 6) try { d.Color = FrameRenderer.ParseColor(color.Text.Trim().TrimStart('#')); } catch (ArgumentException) { }
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            color.Text = $"{d.Color.R:X2}{d.Color.G:X2}{d.Color.B:X2}";
            applyHint = "Default RGB color changed · port overrides and animated palettes are unchanged";
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    private static void UpdateReadOnlyText(TextBox control, string value)
    {
        if (control.Text == value) return;
        int start = control.SelectionStart, length = control.SelectionLength;
        int firstLine = control.IsHandleCreated ? (int)SendMessage(control.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero) : 0; // EM_GETFIRSTVISIBLELINE
        control.Text = value;
        control.Select(Math.Min(start, control.TextLength), Math.Min(length, Math.Max(0, control.TextLength - start)));
        if (control.IsHandleCreated)
        {
            int now = (int)SendMessage(control.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero);
            SendMessage(control.Handle, 0xB6, IntPtr.Zero, (IntPtr)(firstLine - now)); // EM_LINESCROLL
        }
    }
    private void WriteAcceptanceStatus()
    {
        if (AcceptanceStatusPath is null || DateTime.UtcNow < nextStatusWrite) return;
        nextStatusWrite = DateTime.UtcNow.AddSeconds(5);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AcceptanceStatusPath)!);
            File.WriteAllText(AcceptanceStatusPath, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, engine.Running, engine.Live, engine.Cancelled, administrator = StartupManager.Elevated, telemetry = engine.Latest, fans = engine.Fans.Values.OrderBy(x => x.Port), fanCommands = engine.FanCommands.Values.OrderBy(x => x.Port), status = engine.Status, log = engine.Logs.TakeLast(12) }, Settings.JsonOptions));
        }
        catch (Exception error) { session.Text = "Acceptance status output failed: " + error.Message; }
    }
    private async Task RestartElevated() => await Run(async () =>
    {
        if (MessageBox.Show("Restart with administrator rights for CPU sensor access?\n\nThe current hardware session will stop first. The new window opens in Preview; confirm live control again. Windows may show a UAC prompt.", "Restart ThermalDesk", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var next = ReadSettings(); SettingsStore.Save(configPath, next); await engine.Stop();
        var launch = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas", Arguments = $"--preview --wait-for-process {Environment.ProcessId}" };
        using var child = Process.Start(launch) ?? throw new IOException("Elevated restart did not launch.");
        settings = next; exiting = true; BeginInvoke((Action)Close);
    });
    private void ProfileMenu()
    {
        var menu = ReplacePopup(ref profileMenu);
        menu.Items.Add("Import profile… (load into this window)", null, (_, _) =>
        {
            try
            {
                using var d = new OpenFileDialog { Title = "Import ThermalDesk profile", Filter = "ThermalDesk profiles|*.json;*.previous|All files|*.*", InitialDirectory = Path.GetDirectoryName(configPath), CheckFileExists = true };
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var candidate = SettingsStore.LoadForImport(d.FileName, settings.StartLiveAtSignIn, settings.ResumeLastProfileOnLaunch);
                string summary = $"Load {Path.GetFileName(d.FileName)}?\n\nCooling: {(candidate.CoolingEnabled ? "enabled" : "disabled")} · configured ports: {candidate.Ports.Count(p => p.Mode != ControlMode.Preserve)}\nRGB: {(candidate.RgbEnabled ? "enabled" : "disabled")}\n3.9-inch LCD: {(candidate.Panel.Enabled ? "enabled" : "disabled")} · AX LCD: {(candidate.Aio.Enabled ? "enabled" : "disabled")}\n\nReplaces this window's settings immediately. A dated backup is kept. Windows startup is unchanged.\n" +
                    (engine.Live && engine.Running ? "Compatible changes apply in the existing Live session. Different cooling ownership/wiring is blocked; Stop first for those changes." : "Does not start hardware. Review the settings, then choose Start live control when ready.");
                int onlineCount = OnlineMediaPolicy.Sources(candidate).Select(OnlineMediaPolicy.Key).Distinct().Count();
                if (onlineCount > 0) summary += $"\n\nOnline sources: {onlineCount}. Live mode will make HTTP(S) requests to URLs from this profile. Import trusted profiles only.";
                if (candidate.RgbScheduleEnabled) summary += "\n\nRGB schedule enabled: local PC time; may dim/turn off managed lighting.";
                if (MessageBox.Show(this, summary, "Import profile", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                string backup = ImportProfile(d.FileName);
                Info("Profile loaded into this window and saved. No restart is needed.\n\n" + (engine.Live ? "Applied to the existing Live session without reconnecting the fan controller." : "Hardware was not started. Review Displays and Cooling + RGB, then choose Start live control.") + "\n\nPrevious profile backup:\n" + backup);
            }
            catch (Exception e) { Error(e); }
        });
        menu.Items.Add("Export current profile…", null, (_, _) => { try { using var d = new SaveFileDialog { Filter = "JSON profile|*.json", FileName = "thermaldesk-profile.json" }; if (d.ShowDialog(this) == DialogResult.OK) { SettingsStore.Save(d.FileName, ReadSettings()); Info("Current settings exported to:\n" + d.FileName); } } catch (Exception e) { Error(e); } });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open profile folder", null, (_, _) => { try { Directory.CreateDirectory(Path.GetDirectoryName(configPath)!); using var process = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "\"" + Path.GetDirectoryName(configPath) + "\"" }); } catch (Exception e) { Error(e); } });
        menu.Show(Cursor.Position);
    }
    private string ImportProfile(string source)
    {
        if (busy || exiting) throw new InvalidOperationException("Wait for the current operation to finish before importing.");
        var next = SettingsStore.LoadForImport(source, settings.StartLiveAtSignIn, settings.ResumeLastProfileOnLaunch);
        next.MinimizeAfterStartup = settings.MinimizeAfterStartup;
        next.MinimizeWhenClosed = settings.MinimizeWhenClosed;
        next.ProcessMonitorEnabled = settings.ProcessMonitorEnabled; next.ProcessSampleSeconds = settings.ProcessSampleSeconds;
        next.LogSampleSeconds = settings.LogSampleSeconds;
        next.ShowTechnicalDetails = settings.ShowTechnicalDetails;
        if (engine.Running && engine.Live) engine.ValidateLiveSettings(next);
        string backup = SettingsStore.BackupBeforeImport(configPath, settings);
        var previous = settings.Copy();
        settingsDelay.Stop();
        SettingsStore.Save(configPath, next);
        try { if (engine.Running && engine.Live) engine.ApplyLiveSettings(next); }
        catch { SettingsStore.Save(configPath, previous); throw; }
        loadingProfile = true;
        try
        {
            ports.CancelEdit(); settings = next;
            cooling.Checked = next.CoolingEnabled; wiring.Checked = next.CoolingWiringConfirmed; rgb.Checked = next.RgbEnabled; monitorFans.Checked = next.MonitorFanController;
            color.Text = next.RgbColor; serial.Text = next.ControllerSerial; poll.Value = next.PollSeconds;
            sensorSelector.LoadSelection(next.PreferredGpuId, next.AllowMonitoringOnly);
            powerExtra.Value = (decimal)next.PowerEstimateExtraWatts; powerEfficiency.Value = (decimal)next.PowerEstimateEfficiencyPercent; powerGpu.Checked = next.PowerEstimateIncludesGpu;
            panelEditor.LoadProfile(next.Panel); aioEditor.LoadProfile(next.Aio);
            portDesign.Clear();
            foreach (var port in next.Ports)
            {
                portDesign[port.Port] = port.Copy();
                var row = ports.Rows.Cast<DataGridViewRow>().Single(r => Convert.ToInt32(r.Cells[0].Value) == port.Port);
                row.SetValues(port.Port, port.Role.ToString(), port.Mode.ToString(), port.Source.ToString(), port.MinimumDuty, port.FixedDuty, port.Rgb, port.LedCount,
                    string.Join(';', port.Curve.Select(x => $"{x.Temperature.ToString(CultureInfo.InvariantCulture)}:{x.Duty.ToString(CultureInfo.InvariantCulture)}")));
            }
        }
        finally { loadingProfile = false; settingsDelay.Stop(); }
        startupWarning = "";
        applyHint = "Imported " + Path.GetFileName(source) + (engine.Live ? " · applied in place" : " · ready for review");
        RefreshData();
        return backup;
    }
    private void ConfigureStartup()
    {
        var menu = ReplacePopup(ref startupMenu);
        menu.Items.Add(StartupManager.State()).Enabled = false;
        var after = new ToolStripMenuItem("Minimize after startup") { CheckOnClick = true, Checked = minimizeAfterStartup.Checked }; menu.Items.Add(after);
        after.Click += (_, _) => minimizeAfterStartup.Checked = after.Checked;
        var closed = new ToolStripMenuItem("Minimize to tray when closed") { CheckOnClick = true, Checked = minimizeWhenClosed.Checked }; menu.Items.Add(closed);
        closed.Click += (_, _) => minimizeWhenClosed.Checked = closed.Checked;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start preview at sign-in", null, (_, _) =>
        { try { StartupManager.Configure(false); settings.StartLiveAtSignIn = false; SettingsStore.Save(configPath, ReadSettings()); Info("Preview startup registered for this Windows user."); } catch (Exception e) { Error(e); } });
        menu.Items.Add("Start saved LIVE profile at sign-in…", null, (_, _) =>
        {
            try
            {
                var next = ReadSettings(); next.StartLiveAtSignIn = true; next.ValidateLiveSelection();
                if (MessageBox.Show("Start the saved live profile automatically after Windows sign-in?\n\nValidate every configured device and independent cooling protection first. The task runs elevated for sensor access. Close/disable vendor auto-start yourself only after hardware tests pass. Conflicts block startup; TT is never killed. This is not a before-login cooling service.\n\nHold Shift during launch to bypass live startup. Missing temperatures prevent takeover or release captured duties; they never invent a full-speed command.", "Confirm automatic live startup", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                var old = settings.Copy(); SettingsStore.Save(configPath, next);
                try { StartupManager.Configure(true); settings = next; Info("Elevated live sign-in task registered. Saved devices only; next sign-in will apply it."); }
                catch { SettingsStore.Save(configPath, old); throw; }
            }
            catch (Exception e) { Error(e); }
        });
        menu.Items.Add("Remove ThermalDesk startup", null, (_, _) =>
        { try { StartupManager.Remove(); settings.StartLiveAtSignIn = false; SettingsStore.Save(configPath, ReadSettings()); Info("ThermalDesk startup removed. Other applications were not changed."); } catch (Exception e) { Error(e); } });
        // ToolStrip closes before dispatching an item's Click. Disposing in Closed
        // destroys that item while WinForms is still dispatching its action.
        menu.Show(Cursor.Position);
    }
    private static ContextMenuStrip ReplacePopup(ref ContextMenuStrip? previous)
    {
        previous?.Close(); previous?.Dispose();
        return previous = new OwnedPopupMenu();
    }
    private void ExportDiagnostics()
    {
        using var d = new SaveFileDialog { Filter = "JSON diagnostic report|*.json", FileName = "thermaldesk-diagnostics.json" };
        if (d.ShowDialog() != DialogResult.OK) return;
        File.WriteAllText(d.FileName, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, devices = HidLink.Inventory(), session = engine.Status, telemetry = engine.Latest, fans = engine.Fans, log = engine.Logs }, Settings.JsonOptions));
    }
    private void HideToTray() { tray.Visible = true; refresh.Interval = 5000; Hide(); }
    private void RestoreFromTray() { refresh.Interval = 1000; Show(); WindowState = FormWindowState.Normal; Activate(); if (!busy) RefreshData(); }
    private void RequestExit() { exiting = true; Close(); }
    private bool TryHideOnClose(CloseReason reason)
    {
        if (!StartupManager.ShouldHideOnClose(minimizeWhenClosed.Checked, reason, exiting)) return false;
        if (settingsDelay.Enabled) ApplyPendingSettings();
        HideToTray(); return true;
    }
    private async void ClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (closeCompleted) return;
        if (TryHideOnClose(e.CloseReason)) { e.Cancel = true; return; }
        e.Cancel = true;
        if (busy) return;
        if (settingsDelay.Enabled) ApplyPendingSettings();
        busy = true;
        try { await engine.Stop(); exiting = closeCompleted = true; refresh.Stop(); settingsDelay.Stop(); tray.Visible = false; BeginInvoke((Action)Close); }
        catch (Exception error) { exiting = false; RestoreFromTray(); Error(error); } finally { busy = false; }
    }
    public void RenderPreview(string path)
    {
        ShowOffscreenForTest();
        engine.Start(settings, false); Thread.Sleep(80); RefreshData();
        Application.DoEvents();
        using var b = new Bitmap(Width, Height); DrawToBitmap(b, new Rectangle(Point.Empty, Size));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); b.Save(path);
        Task.Run(async () => await engine.Stop()).GetAwaiter().GetResult();
    }
    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr process, int flags);
    public void Benchmark(string path)
    {
        ShowOffscreenForTest(); engine.Start(settings, false);
        var samples = new List<object>();
        using var p = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew(); TimeSpan? baseline = null; double baselineTime = 0;
        for (int second = 0; second <= 35; second++)
        {
            RefreshData(); Application.DoEvents();
            if (second == 5) { baseline = p.TotalProcessorTime; baselineTime = clock.Elapsed.TotalSeconds; }
            if (second >= 5 && second % 5 == 0)
            {
                p.Refresh();
                samples.Add(new { seconds = second, workingSetMiB = p.WorkingSet64 / 1048576d, privateMiB = p.PrivateMemorySize64 / 1048576d, threads = p.Threads.Count, handles = p.HandleCount, gdiObjects = GetGuiResources(p.Handle, 0), userObjects = GetGuiResources(p.Handle, 1) });
            }
            if (second < 35) Thread.Sleep(1000);
        }
        double cpuPercent = (p.TotalProcessorTime - baseline!.Value).TotalSeconds / (clock.Elapsed.TotalSeconds - baselineTime) / Environment.ProcessorCount * 100;
        Task.Run(async () => await engine.Stop()).GetAwaiter().GetResult();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { hardwareAccess = false, workload = "Simulated UI: 2 display previews refreshed once/second. No real sensor or USB work.", logicalProcessors = Environment.ProcessorCount, averageTotalCpuPercent = cpuPercent, samples }, Settings.JsonOptions));
    }
    private void ShowOffscreenForTest()
    {
        smokeMode = true; StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000); ShowInTaskbar = false;
        Show(); PerformLayout(); Application.DoEvents();
    }
    private static void Error(Exception e) => MessageBox.Show(e.Message, "ThermalDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    private static void Info(string text) => MessageBox.Show(text, "ThermalDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
    internal int ExerciseAutomaticSettingsForTest()
    {
        // Real UI change events and WinForms timer; never Show/Start this form's engine.
        void WaitFor(Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < 4000) { Application.DoEvents(); Thread.Sleep(10); }
            if (!done()) throw new InvalidOperationException("Automatic UI change did not complete within four seconds.");
        }
        smokeMode = false;
        try
        {
            poll.Value = 3;
            WaitFor(() => File.Exists(configPath) && SettingsStore.Load(configPath).PollSeconds == 3);
            if (engine.Live || engine.Running) throw new InvalidOperationException("Editing preview opened hardware or started a session.");
            cooling.Checked = true;
            WaitFor(() => applyHint.StartsWith("Pending"));
            if (SettingsStore.Load(configPath).CoolingEnabled) throw new InvalidOperationException("Invalid automatic change was persisted.");
            cooling.Checked = false; poll.Value = 4; poll.Value = 5;
            WaitFor(() => SettingsStore.Load(configPath).PollSeconds == 5);
            if (!applyHint.StartsWith("Profile saved")) throw new InvalidOperationException("Valid correction was not automatically applied.");
            return 3;
        }
        finally { smokeMode = true; settingsDelay.Stop(); }
    }
    internal int ExerciseUiLifecycleForTest(string screenshot)
    {
        int checks = 0;
        void Verify(bool value, string name) { checks++; if (!value) throw new InvalidOperationException("UI lifecycle: " + name); }
        var menu = (OwnedPopupMenu)ReplacePopup(ref startupMenu);
        Verify(menu.ExerciseClosedClickForTest(), "startup item survives Closed before Click");
        var replacement = ReplacePopup(ref startupMenu);
        Verify(menu.IsDisposed && !replacement.IsDisposed, "previous popup is released on replacement, not on dispatch");
        ShowOffscreenForTest();
        using var text = new TextBox { Multiline = true, Text = "first\r\nsecond\r\nthird" };
        text.CreateControl(); text.Select(7, 3); UpdateReadOnlyText(text, text.Text + "\r\nfourth");
        Verify(text.SelectionStart == 7 && text.SelectionLength == 3, "changing diagnostic text preserves selection");
        int statusChanges = 0; session.TextChanged += (_, _) => statusChanges++;
        RefreshData(); statusChanges = 0;
        var bounds = session.Bounds;
        for (int i = 0; i < 12; i++) RefreshData();
        Verify(statusChanges == 0 && session.Bounds == bounds && !session.AutoSize && !resources.AutoSize, "stable refresh does not rewrite or resize session/footer");
        Verify(DoubleBuffered && ports is BufferedDataGridView, "form and feedback grid are buffered");
        Verify(Icon is not null && tray.Icon is not null && hints.GetToolTip(rgb)?.Contains("Global RGB") == true, "app/tray icon and master lighting hint are available");
        var rgbRow = ports.Rows[0]; var originalPort = ReadPort(rgbRow);
        rgbRow.Cells[6].Value = true;
        Verify(ReadPort(rgbRow).Rgb && !ReadPort(rgbRow).RgbOffRequested, "grid ON clears explicit OFF");
        rgbRow.Cells[6].Value = false;
        Verify(!ReadPort(rgbRow).Rgb && ReadPort(rgbRow).RgbOffRequested, "grid OFF is intentional, not unmanaged");
        var unchangedPort = originalPort.Copy(); unchangedPort.Rgb = false; unchangedPort.RgbOffRequested = false;
        AcceptPortEditorResult(rgbRow, unchangedPort);
        Verify(!ReadPort(rgbRow).RgbOffRequested, "editor Leave unchanged survives grid refresh");
        AcceptPortEditorResult(rgbRow, originalPort);
        checks += panelEditor.ExerciseOfflineEditorForTest(); checks += aioEditor.ExerciseOfflineEditorForTest();
        var tabs = Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
        tabs.SelectedIndex = 2; Size = MinimumSize; PerformLayout(); Application.DoEvents();
        using var picture = new Bitmap(Width, Height); DrawToBitmap(picture, new Rectangle(Point.Empty, Size)); picture.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
        return checks;
    }
    internal int ExerciseProfileImportForTest(string directory)
    {
        int checks = 0;
        void Verify(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Profile import: " + message); }
        ShowOffscreenForTest();
        var candidate = new Settings { PollSeconds = 4, RgbEnabled = true, CoolingEnabled = true, CoolingWiringConfirmed = true, PreferredGpuId = "/saved/test", StartLiveAtSignIn = true, ResumeLastProfileOnLaunch = true };
        foreach (var port in candidate.Ports) { port.Role = PortRole.Fan; FanPresets.Apply(port, FanPreset.Silent); port.Source = TemperatureSource.Cpu; }
        candidate.Ports[0].Rgb = true; candidate.Ports[0].RgbEffect = RgbEffect.PaletteWave; candidate.Ports[0].RgbPalette = ["123456", "ABCDEF"];
        candidate.Panel = LcdPresets.Create(new DisplaySettings { Enabled = true, Brightness = 42, FramesPerSecond = 3 }, "tall-rings", 480, 128);
        candidate.Aio = LcdPresets.Create(new DisplaySettings { Enabled = true, Rotation = 180 }, "round-thermal", 480, 480);
        string source = Path.Combine(directory, "old-version-profile.json"); SettingsStore.Save(source, candidate);
        SettingsStore.Save(configPath, settings); string before = File.ReadAllText(configPath);
        smokeMode = false;
        try
        {
            // A pending edit used to overwrite the disk-only import on the next timer tick.
            poll.Value = 3; Verify(settingsDelay.Enabled, "old UI edit is pending");
            string backup = ImportProfile(source);
            var read = ReadSettings();
            Verify(read.PollSeconds == 4 && read.CoolingEnabled && read.RgbEnabled, "import updates visible controls immediately");
            Verify(read.ResumeLastProfileOnLaunch != true, "import cannot enable automatic manual launch on this PC");
            Verify(read.Ports.All(p => FanPresets.Identify(p) == FanPreset.Silent && p.Source == TemperatureSource.Cpu), "every imported curve and source is loaded into the grid");
            Verify(read.Ports[0].RgbPalette.SequenceEqual(candidate.Ports[0].RgbPalette), "editor-only port settings survive UI loading");
            Verify(read.Panel.Rotation == 90 && read.Panel.Widgets.Count == candidate.Panel.Widgets.Count && read.Panel.Widgets[0].X == candidate.Panel.Widgets[0].X, "portrait widgets are not reframed a second time");
            Verify(read.Panel.Brightness == 42 && read.Panel.FramesPerSecond == 3 && read.Aio.Rotation == 180, "both LCD editors reload all display fields");
            Verify(read.PreferredGpuId == "/saved/test" && !read.StartLiveAtSignIn, "GPU choice loads but startup intent stays local");
            Verify(!engine.Running && !engine.Live && !settingsDelay.Enabled, "import starts no hardware and clears pending autosave");
            Verify(File.ReadAllText(backup) == before, "exact previous file is retained in dated backup");
            poll.Value = 5; ApplyPendingSettings();
            var saved = SettingsStore.Load(configPath);
            Verify(saved.PollSeconds == 5 && saved.Panel.Rotation == 90 && saved.CoolingEnabled && saved.Ports[0].RgbPalette.SequenceEqual(candidate.Ports[0].RgbPalette), "subsequent edit saves imported settings, not stale controls");
            string savedBytes = File.ReadAllText(configPath);
            string invalid = Path.Combine(directory, "not-a-profile.json"); File.WriteAllText(invalid, "{}");
            bool blocked = false; try { ImportProfile(invalid); } catch (InvalidDataException) { blocked = true; }
            Verify(blocked && File.ReadAllText(configPath) == savedBytes && ReadSettings().PollSeconds == 5, "failed import leaves UI and saved configuration intact");
            string folder = Path.GetDirectoryName(configPath)!;
            Verify(Directory.GetFiles(folder, Path.GetFileName(configPath) + ".before-import-*.json").Contains(backup), "backup is still available after later saves");
        }
        finally { smokeMode = true; settingsDelay.Stop(); Hide(); }
        return checks;
    }
    internal int ExerciseWindowPreferencesForTest(string screenshot)
    {
        int count = 0;
        void Check(bool value, string text) { count++; if (!value) throw new InvalidOperationException("Window settings: " + text); }
        ShowOffscreenForTest();
        var tabs = Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
        tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "Settings");
        smokeMode = false;
        try
        {
            minimizeAfterStartup.Checked = true; minimizeWhenClosed.Checked = true; processInterval.Value = 10;
            resumeLastProfile.Checked = true; technicalDetails.Checked = true;
            ApplyPendingSettings(); var saved = SettingsStore.Load(configPath);
            Check(saved.MinimizeAfterStartup == true && saved.MinimizeWhenClosed && saved.ProcessSampleSeconds == 10, "checkboxes and interval save automatically");
            Check(saved.ResumeLastProfileOnLaunch == true && saved.ShowTechnicalDetails, "resume and readable diagnostic preferences save automatically");
            Check(!engine.Running && !engine.Live && !saved.ProcessMonitorEnabled, "window settings never arm hardware or process scanning");
            Check(!TryHideOnClose(CloseReason.WindowsShutDown) && Visible, "Windows shutdown is never minimized away");
            Close(); Application.DoEvents();
            Check(!Visible && !IsDisposed && tray.Visible && !engine.Running, "real X/Close hides window, retaining app and tray");
            Check(refresh.Interval == 5000, "hidden window reduces UI timer wakeups without changing control polling");
            RestoreFromTray();
            Check(Visible && WindowState == FormWindowState.Normal, "tray Open restores the normal window");
            Check(refresh.Interval == 1000, "visible window restores responsive clock/UI refresh");
            minimizeAfterStartup.Checked = false; ApplyPendingSettings();
            Check(SettingsStore.Load(configPath).MinimizeAfterStartup == false, "OFF is explicitly persisted for legacy task override");
            PerformLayout(); Application.DoEvents(); using var picture = new Bitmap(Width, Height); DrawToBitmap(picture, new Rectangle(Point.Empty, Size)); picture.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
            // Exercise the actual tray action, not a separate synthetic shutdown helper.
            ((ToolStripMenuItem)tray.ContextMenuStrip!.Items[^1]).PerformClick();
            var watch = Stopwatch.StartNew(); while (!IsDisposed && watch.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(10); }
            Check(IsDisposed && closeCompleted, "tray Exit actually closes even with minimize-on-close enabled");
            return count;
        }
        finally { smokeMode = true; if (!IsDisposed) settingsDelay.Stop(); }
    }
    internal int VerifyRecoveryUiForTest(string screenshot)
    {
        RenderPreview(screenshot);
        if (!session.Text.Contains("PROFILE RECOVERY") || !log.Text.StartsWith("STARTUP RECOVERY:")) throw new InvalidOperationException("Recovery reason is not visible in the UI.");
        if (cooling.Checked || rgb.Checked || monitorFans.Checked || engine.Live) throw new InvalidOperationException("Recovery UI armed a hardware channel.");
        return 2;
    }
    internal int ExerciseLiveAutomaticSettingsForTest(string screenshot)
    {
        int checks = 0;
        void Verify(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Live UI: " + message); }
        void WaitFor(Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < 6000) { Application.DoEvents(); Thread.Sleep(10); }
            Verify(done(), "automatic change timed out");
        }
        IEnumerable<Control> Descendants(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
        ShowOffscreenForTest(); Descendants(this).OfType<TabControl>().Single().SelectedIndex = 1;
        smokeMode = false;
        try
        {
            ports.CurrentCell = ports.Rows[0].Cells[2]; ports.BeginEdit(true);
            var modeEditor = ports.EditingControl as ComboBox ?? throw new InvalidOperationException("Expected an active grid combo editor.");
            modeEditor.SelectedItem = nameof(ControlMode.Curve);
            // Notify the same dirty transition produced by the native combo editor.
            ports.NotifyCurrentCellDirty(true);
            WaitFor(() => File.Exists(configPath) && SettingsStore.Load(configPath).Ports[0].Mode == ControlMode.Curve);
            Verify(ports.CurrentCell == ports.Rows[0].Cells[2], "combo edit applies before focus leaves the cell");
            ports.EndEdit();
            Descendants(this).OfType<Button>().Single(b => b.Text == "Silent").PerformClick();
            WaitFor(() => engine.FanCommands.Count == 5 && engine.FanCommands.Values.All(c => c.TargetDuty == 30));
            Verify(SettingsStore.Load(configPath).Ports.All(p => FanPresets.Identify(p) == FanPreset.Silent), "all five Silent presets persisted without Save/Stop");
            Verify(engine.Running && engine.Live && !engine.ControllerFault && !liveButton.Enabled, "same live session remains active");
            var row = ports.Rows[0]; var extended = ReadPort(row);
            extended.RgbEffect = RgbEffect.PaletteWave; extended.RgbPalette = ["112233", "AA00FF"]; extended.RgbDirection = RgbDirection.Reverse;
            AcceptPortEditorResult(row, extended);
            WaitFor(() => SettingsStore.Load(configPath).Ports[0].RgbEffect == RgbEffect.PaletteWave);
            Verify(SettingsStore.Load(configPath).Ports[0].RgbDirection == RgbDirection.Reverse, "editor-only fields applied even with identical grid values");
            RefreshData();
            Verify(row.Cells[10].Value!.ToString()!.Contains("→ 30") && row.Cells[12].Value!.ToString() != "—", "requested target and RPM visible");
            Verify(!settingsDelay.Enabled, "read-only feedback does not schedule another configuration apply");
            Verify(panelPreview.Image is null && aioPreview.Image is null, "hidden LCD previews do not render while editing cooling");
            Descendants(this).OfType<Button>().Single(b => b.Text == "Custom").PerformClick();
            WaitFor(() => engine.FanCommands.Values.All(c => c.TargetDuty == 45));
            Verify(SettingsStore.Load(configPath).Ports.All(p => p.Curve.All(point => point.Duty == 45)), "Custom restores every original curve");
            minimizeWhenClosed.Checked = true; ApplyPendingSettings();
            Close(); Application.DoEvents();
            Verify(!Visible && !IsDisposed && engine.Running && engine.Live && !engine.Cancelled, "X hides the actual mock Live form without stopping its session");
            RestoreFromTray(); minimizeWhenClosed.Checked = false; ApplyPendingSettings();
            Verify(Visible && engine.Running && engine.Live, "tray restore keeps the same live controller");
            var import = ReadSettings(); import.Ports[0].RgbPalette = ["00FF00", "0000FF"]; import.PollSeconds = 3;
            string importPath = configPath + ".source.json"; SettingsStore.Save(importPath, import);
            string backup = ImportProfile(importPath);
            Verify(engine.Running && engine.Live && ReadSettings().PollSeconds == 3 && File.Exists(backup), "compatible import applies in the same live session");
            string currentBytes = File.ReadAllText(configPath);
            import.CoolingEnabled = false; SettingsStore.Save(importPath, import);
            bool blocked = false; try { ImportProfile(importPath); } catch (InvalidOperationException) { blocked = true; }
            Verify(blocked && File.ReadAllText(configPath) == currentBytes && ReadSettings().CoolingEnabled, "live ownership-changing import leaves controls and file untouched");
            RefreshData(); using var image = new Bitmap(Width, Height); DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
            return checks;
        }
        finally { smokeMode = true; settingsDelay.Stop(); Hide(); }
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { if (!engine.Running) engine.OnlineMedia.Dispose(); startupMenu?.Dispose(); profileMenu?.Dispose(); settingsDelay.Dispose(); refresh.Dispose(); tray.ContextMenuStrip?.Dispose(); tray.Dispose(); panelRenderer.Dispose(); aioRenderer.Dispose(); panelPreview.Image?.Dispose(); aioPreview.Image?.Dispose(); hints.Dispose(); appIcon.Dispose(); } base.Dispose(disposing); }
}

internal sealed class DisplayEditor : GroupBox
{
    private readonly ToolTip hints = new() { AutoPopDelay = 20000, InitialDelay = 450, ShowAlways = true };
    public event Action? SettingsChanged;
    private readonly CheckBox enabled = new() { Text = "Enable this LCD", AutoSize = true }, overlay = new() { Text = "Metrics overlay", AutoSize = true };
    private readonly CheckBox custom = new() { Text = "Use custom widget layout", AutoSize = true };
    private readonly ComboBox content = new() { DropDownStyle = ComboBoxStyle.DropDownList }, rotation = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox path = new();
    private readonly NumericUpDown brightness = new() { Minimum = 0, Maximum = 100 };
    private readonly NumericUpDown frames = new() { Minimum = 1, Maximum = 10 };
    private readonly CheckBox cycle = new() { Text = "Rotate widget pages", AutoSize = true };
    private readonly NumericUpDown cycleSeconds = new() { Minimum = 1, Maximum = 3600 };
    private readonly Label connection = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.Silver, Text = "Offline editor · hardware is opened only in a live session" };
    private readonly Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Button editWidgets = new() { Text = "Edit widgets…", AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox layoutPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 360 };
    private readonly Button previewPreset = new() { Text = "Preview preset…", AutoSize = true, FlatStyle = FlatStyle.Flat };
    private bool acceptingLayout;
    private DisplaySettings design;
    public DisplayEditor(string title, DisplaySettings s, int width, int height, Func<Telemetry> telemetry)
    {
        design = s.Copy();
        Text = title; Dock = DockStyle.Fill; ForeColor = Color.White; Padding = new Padding(16); Margin = new Padding(5);
        var grid = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 14 };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 115)); grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int row = 0; row < 14; row++) grid.RowStyles.Add(new(SizeType.Absolute, row == 13 ? 60 : row == 2 ? 46 : 38));
        Controls.Add(scroll); scroll.Controls.Add(grid);
        content.Items.AddRange(Enum.GetNames<DisplayContent>()); rotation.Items.AddRange(["0", "90", "180", "270"]);
        enabled.Checked = s.Enabled; overlay.Checked = s.Overlay; custom.Checked = s.UseCustomLayout; content.SelectedItem = s.Content.ToString(); rotation.SelectedItem = s.Rotation.ToString(); brightness.Value = s.Brightness; frames.Value = s.FramesPerSecond; path.Text = s.MediaPath;
        grid.Controls.Add(enabled, 0, 0); grid.SetColumnSpan(enabled, 2);
        layoutPreset.Items.AddRange(LcdPresets.Available(width, height).Cast<object>().ToArray());
        layoutPreset.SelectedIndex = 0; layoutPreset.Dock = DockStyle.Fill;
        grid.Controls.Add(new Label { Text = "Layout preset", AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 0, 1);
        grid.Controls.Add(layoutPreset, 1, 1);
        var layoutActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        layoutActions.Controls.Add(previewPreset); layoutActions.Controls.Add(editWidgets);
        grid.Controls.Add(layoutActions, 0, 2); grid.SetColumnSpan(layoutActions, 2);
        var fields = new (string, Control)[] { ("Content", content), ("Media file", path), ("Brightness %", brightness), ("Rotation °", rotation), ("Frames / sec", frames), ("", overlay) };
        for (int i = 0; i < fields.Length; i++) { grid.Controls.Add(new Label { Text = fields[i].Item1, AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 0, i + 3); fields[i].Item2.Dock = DockStyle.Fill; grid.Controls.Add(fields[i].Item2, 1, i + 3); }
        rotation.AccessibleDescription = width == height ? "Clockwise rotation in degrees." : "0 or 180: landscape. 90 or 270: portrait. The preset supplies a layout designed for that orientation.";
        var browse = new Button { Text = "Choose image / GIF", AutoSize = true, FlatStyle = FlatStyle.Flat };
        browse.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.gif;*.bmp" }; if (d.ShowDialog() == DialogResult.OK) { path.Text = d.FileName; content.SelectedItem = "Image"; } };
        grid.Controls.Add(browse, 1, 9);
        grid.Controls.Add(custom, 1, 10);
        cycle.Checked = s.RotateWidgetGroups; cycleSeconds.Value = s.WidgetGroupSeconds;
        grid.Controls.Add(cycle, 0, 11); grid.SetColumnSpan(cycle, 2);
        grid.Controls.Add(new Label { Text = "Page seconds", AutoSize = true }, 0, 12); grid.Controls.Add(cycleSeconds, 1, 12);
        grid.Controls.Add(connection, 0, 13); grid.SetColumnSpan(connection, 2);
        editWidgets.Click += (_, _) =>
        {
            try
            {
                using var editor = new WidgetDesignerForm(title, Read(), width, height, telemetry);
                if (editor.ShowDialog(FindForm()) == DialogResult.OK) AcceptLayout(editor.Result);
            }
            catch (Exception error) { MessageBox.Show(error.Message, "Widget designer", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        previewPreset.Click += (_, _) =>
        {
            try
            {
                if (layoutPreset.SelectedItem is not LcdPreset preset) return;
                var candidate = LcdPresets.Create(Read(), preset.Id, width, height);
                using var editor = new WidgetDesignerForm(title + " · " + preset.Name, candidate, width, height, telemetry);
                // Cancel leaves every current display/cooling setting unchanged.
                if (editor.ShowDialog(FindForm()) == DialogResult.OK) AcceptLayout(editor.Result);
            }
            catch (Exception error) { MessageBox.Show(error.Message, "Layout preset", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        rotation.SelectedIndexChanged += (_, _) =>
        {
            if (acceptingLayout) return;
            var before = WidgetLayout.Canvas(design, width, height);
            design.Rotation = int.Parse((string)rotation.SelectedItem!, CultureInfo.InvariantCulture);
            var after = WidgetLayout.Canvas(design, width, height);
            if (before != after) WidgetLayout.Reframe(design, before.Width, before.Height, after.Width, after.Height);
        };
        custom.CheckedChanged += (_, _) =>
        {
            if (custom.Checked && design.Widgets.Count == 0)
            { var size = WidgetLayout.Canvas(design, width, height); design.Widgets = WidgetLayout.Defaults(size.Width, size.Height); }
        };
        hints.SetToolTip(enabled, "Enables only this LCD during Live. Editing a disabled LCD is safe and works offline.");
        hints.SetToolTip(layoutPreset, "Choose an orientation-specific template, then preview it before replacing your current layout.");
        hints.SetToolTip(previewPreset, "Opens an editable copy. Cancel preserves the current layout; Apply replaces this display's widgets only.");
        hints.SetToolTip(editWidgets, "Edit text, sensor sources, fonts, colors, gauges and placement. No live hardware is needed for editing.");
        hints.SetToolTip(content, "Dashboard: sensor widgets. Image: selected image/GIF. Screen: primary desktop, which may contain private information.");
        hints.SetToolTip(path, "Local image/GIF path, up to 25 MiB. The file remains on this PC; it is not uploaded.");
        hints.SetToolTip(brightness, "Physical LCD brightness from 0 to 100. Changing it reconnects only this display, not the fan controller.");
        hints.SetToolTip(rotation, rotation.AccessibleDescription);
        hints.SetToolTip(frames, "LCD refresh rate. 1–2 FPS is sufficient for temperatures and uses less CPU/USB bandwidth than 10 FPS.");
        hints.SetToolTip(overlay, "Draw sensor widgets over Image/Screen content. Dashboard always displays its widgets.");
        hints.SetToolTip(custom, "Use your editable widgets instead of the built-in dashboard layout.");
        hints.SetToolTip(cycle, "Assign Page 1, 2… in Edit widgets. Pages rotate; Page 0 stays visible. Disabled rotation shows all widgets. No controller reconnect or fan changes.");
        hints.SetToolTip(cycleSeconds, "Seconds per page (1–3600); 5 is a good start. Independent for each LCD. The editor keeps the selected widget's page visible.");
        foreach (Control label in grid.Controls.OfType<Label>())
        {
            var position = grid.GetPositionFromControl(label);
            if (position.Column == 0 && grid.GetControlFromPosition(1, position.Row) is { } input)
                hints.SetToolTip(label, hints.GetToolTip(input));
        }
    }
    private void AcceptLayout(DisplaySettings layout)
    {
        acceptingLayout = true;
        try
        {
            design = layout.Copy();
            // Do not reframe freshly designed portrait widgets a second time.
            rotation.SelectedItem = design.Rotation.ToString(CultureInfo.InvariantCulture);
            content.SelectedItem = design.Content.ToString();
            cycle.Checked = design.RotateWidgetGroups; cycleSeconds.Value = design.WidgetGroupSeconds;
            custom.Checked = true; overlay.Checked = true;
        }
        finally { acceptingLayout = false; }
        SettingsChanged?.Invoke();
    }
    internal int ExercisePresetsForTest(int width, int height)
    {
        int checks = 0;
        foreach (var preset in LcdPresets.Available(width, height))
        {
            var before = Read();
            var candidate = LcdPresets.Create(before, preset.Id, width, height);
            if (Read().Rotation != before.Rotation || Read().Widgets.Count != before.Widgets.Count)
                throw new InvalidOperationException("Previewing a preset modified the display.");
            checks++;
            AcceptLayout(candidate);
            var after = Read(); WidgetLayout.Validate(after, width, height);
            if (after.Rotation != candidate.Rotation || after.Widgets[0].X != candidate.Widgets[0].X ||
                after.Widgets[0].Width != candidate.Widgets[0].Width || after.Enabled != before.Enabled ||
                after.Brightness != before.Brightness || after.FramesPerSecond != before.FramesPerSecond)
                throw new InvalidOperationException("Preset acceptance reframed widgets twice or changed device controls.");
            checks++;
        }
        if (!previewPreset.Enabled || layoutPreset.Items.Count != LcdPresets.Available(width, height).Count)
            throw new InvalidOperationException("Preset controls must remain available offline.");
        return checks + 1;
    }
    internal void UpdateConnectionStatus(string text) { if (connection.Text != text) connection.Text = text; }
    internal int ExerciseOfflineEditorForTest()
    {
        if (!editWidgets.Enabled || !scroll.AutoScroll || editWidgets.Top > 100) throw new InvalidOperationException("Offline LCD widget editor is hidden or disabled.");
        UpdateConnectionStatus("Not detected · reconnect the device; its layout remains editable offline");
        if (!connection.Text.Contains("editable offline")) throw new InvalidOperationException("LCD connection status was not updated.");
        return 2;
    }
    public DisplaySettings Read()
    {
        var result = design.Copy(); result.Enabled = enabled.Checked; result.Content = Enum.Parse<DisplayContent>((string)content.SelectedItem!); result.MediaPath = path.Text.Trim(); result.Brightness = (int)brightness.Value; result.FramesPerSecond = (int)frames.Value; result.Rotation = int.Parse((string)rotation.SelectedItem!, CultureInfo.InvariantCulture); result.Overlay = overlay.Checked; result.UseCustomLayout = custom.Checked;
        result.RotateWidgetGroups = cycle.Checked; result.WidgetGroupSeconds = (int)cycleSeconds.Value; return result;
    }
    public void LoadProfile(DisplaySettings value)
    {
        acceptingLayout = true;
        try
        {
            design = value.Copy(); enabled.Checked = value.Enabled; overlay.Checked = value.Overlay;
            content.SelectedItem = value.Content.ToString(); rotation.SelectedItem = value.Rotation.ToString(CultureInfo.InvariantCulture);
            brightness.Value = value.Brightness; frames.Value = value.FramesPerSecond; path.Text = value.MediaPath; custom.Checked = value.UseCustomLayout;
            cycle.Checked = value.RotateWidgetGroups; cycleSeconds.Value = value.WidgetGroupSeconds;
        }
        finally { acceptingLayout = false; }
    }
    protected override void Dispose(bool disposing)
    { if (disposing) hints.Dispose(); base.Dispose(disposing); }
}
