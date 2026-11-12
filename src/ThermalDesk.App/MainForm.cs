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
    private readonly Engine engine = new();
    private readonly string configPath;
    private Settings settings;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer settingsDelay = new() { Interval = 450 };
    private string applyHint = "";
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "ThermalDesk" };
    private readonly Label session = new(), resources = new(), cpu = new(), gpu = new(), memory = new();
    private readonly TextBox deviceStatus = new(), log = new(), color = new(), serial = new();
    private readonly PictureBox panelPreview = new(), aioPreview = new();
    private readonly DataGridView ports = new();
    private readonly Dictionary<int, PortSettings> portDesign = new();
    private readonly CheckBox monitorFans = new() { Text = "Monitor controller RPM (no speed writes)", AutoSize = true };
    private readonly CheckBox cooling = new() { Text = "Enable USB fan/pump control" }, wiring = new() { Text = "I verified every managed port and independent hardware protection" }, rgb = new() { Text = "Enable RGB on selected ports" };
    private readonly NumericUpDown poll = new() { Minimum = 1, Maximum = 10, Width = 70 };
    private readonly DisplayEditor panelEditor, aioEditor;
    private readonly FrameRenderer panelRenderer = new(), aioRenderer = new();
    private readonly Button liveButton;
    private bool busy, exiting, smokeMode;
    private DateTime lastResource = DateTime.UtcNow;
    private readonly DateTime resourceWarmup = DateTime.UtcNow.AddSeconds(5);
    private TimeSpan lastCpu;
    private DateTime nextStatusWrite;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? AcceptanceStatusPath { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool StartMinimized { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool StartupRequested { get; set; }
    public MainForm(Settings initial, string path)
    {
        settings = initial; configPath = path;
        foreach (var port in initial.Ports) portDesign[port.Port] = port.Copy();
        Text = "ThermalDesk · local hardware control"; BackColor = Background; ForeColor = Color.White;
        Font = new Font("Segoe UI", 10); ClientSize = new Size(1180, 820); MinimumSize = new Size(1000, 720);
        StartPosition = FormStartPosition.CenterScreen;
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), RowCount = 5, ColumnCount = 1 };
        shell.RowStyles.Add(new(SizeType.Absolute, 66)); shell.RowStyles.Add(new(SizeType.Absolute, 52));
        shell.RowStyles.Add(new(SizeType.Absolute, 36)); shell.RowStyles.Add(new(SizeType.Percent, 100)); shell.RowStyles.Add(new(SizeType.Absolute, 30));
        Controls.Add(shell);
        shell.Controls.Add(new Label { Text = "THERMALDESK  /  Hardware, without the overhead", ForeColor = Accent, Font = new Font(Font.FontFamily, 21, FontStyle.Bold), AutoSize = true, Padding = new Padding(0, 10, 0, 0) }, 0, 0);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        tools.Controls.Add(Button("Preview", async () => await Switch(false)));
        liveButton = Button("Start live control", async () => await Switch(true), true); tools.Controls.Add(liveButton);
        tools.Controls.Add(Button("Stop", async () => await Run(async () => await engine.Stop())));
        tools.Controls.Add(Button("Save profile", () => { settings = ReadSettings(); SettingsStore.Save(configPath, settings); Info("Profile saved. Valid curve, RGB and widget edits apply automatically in an active live session."); }));
        tools.Controls.Add(Button("Import / export", ProfileMenu));
        tools.Controls.Add(Button("Start at sign-in", ConfigureStartup));
        if (!StartupManager.Elevated) tools.Controls.Add(Button("Restart as admin", async () => await RestartElevated()));
        shell.Controls.Add(tools, 0, 1);
        session.AutoSize = true; session.ForeColor = Muted; shell.Controls.Add(session, 0, 2);
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 8) };
        shell.Controls.Add(tabs, 0, 3);
        var dashboard = Page(tabs, "Overview");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 58)); layout.ColumnStyles.Add(new(SizeType.Percent, 42));
        layout.RowStyles.Add(new(SizeType.Absolute, 108)); layout.RowStyles.Add(new(SizeType.Absolute, 170)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        dashboard.Controls.Add(layout);
        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
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
        var coolingLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Padding = new Padding(12) };
        coolingLayout.RowStyles.Add(new(SizeType.Absolute, 65)); coolingLayout.RowStyles.Add(new(SizeType.Absolute, 64)); coolingLayout.RowStyles.Add(new(SizeType.Percent, 100)); coolingLayout.RowStyles.Add(new(SizeType.Absolute, 80));
        coolingPage.Controls.Add(coolingLayout);
        coolingLayout.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(255, 194, 100), Text = "Identify wiring and keep independent thermal protection. Software cannot guarantee protection after a crash, USB loss or before sign-in.\nCurve/RGB changes apply automatically. Stop restores captured duties, not forced full speed. This is not BIOS hand-back." }, 0, 0);
        var flags = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        cooling.AutoSize = wiring.AutoSize = rgb.AutoSize = true; flags.Controls.AddRange([cooling, rgb, monitorFans, wiring]); coolingLayout.Controls.Add(flags, 0, 1);
        BuildPorts(); coolingLayout.Controls.Add(ports, 0, 2);
        var extras = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        color.Width = 100; serial.Width = 190;
        extras.Controls.AddRange([new Label { Text = "RGB hex", AutoSize = true }, color, Button("Pick color", PickColor), new Label { Text = "Controller serial (optional)", AutoSize = true }, serial]);
        extras.Controls.Add(new Label { Text = "Managed fans:", AutoSize = true });
        foreach (var preset in Enum.GetValues<FanPreset>()) extras.Controls.Add(Button(preset.ToString(), () => ApplyFanPreset(preset)));
        coolingLayout.Controls.Add(extras, 0, 3);
        var displays = Page(tabs, "Displays");
        var displayLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12) };
        displayLayout.ColumnStyles.Add(new(SizeType.Percent, 50)); displayLayout.ColumnStyles.Add(new(SizeType.Percent, 50));
        displayLayout.RowStyles.Add(new(SizeType.Percent, 100)); displayLayout.RowStyles.Add(new(SizeType.Absolute, 68)); displays.Controls.Add(displayLayout);
        panelEditor = new("3.9-inch LCD · 264A:233D", settings.Panel, 480, 128, () => engine.Latest); aioEditor = new("TOUGHLIQUID AX · 264A:2328", settings.Aio, 480, 480, () => engine.Latest);
        displayLayout.Controls.Add(panelEditor, 0, 0); displayLayout.Controls.Add(aioEditor, 1, 0);
        displayLayout.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Muted, Text = "Images: JPEG, PNG, GIF, BMP / up to 25 MiB. GIF playback is capped at the display refresh rate.\nScreen mode sends the primary desktop to the LCD only when you explicitly start live control. No network server or cloud." }, 0, 1); displayLayout.SetColumnSpan(displayLayout.GetControlFromPosition(0, 1)!, 2);
        var logsPage = Page(tabs, "Log + diagnostics"); ConfigureText(log); logsPage.Controls.Add(log);
        var diag = Button("Export diagnostics", ExportDiagnostics); diag.Dock = DockStyle.Bottom; logsPage.Controls.Add(diag); diag.BringToFront();
        var about = Page(tabs, "About");
        var aboutText = new TextBox(); ConfigureText(aboutText); aboutText.Text = $"ThermalDesk {typeof(MainForm).Assembly.GetName().Version?.ToString(3)} — independent experimental controller\r\n\r\nDevices: 264A:233D panel, 264A:2328 AX LCD, 264A:232B five-port fan controller. Unknown descriptors/firmware are rejected.\r\n\r\nNormal launch: Preview, no hardware access. Enable the correct device under Displays before Start live control. CPU sensors on this PC require administrator access and the supported PawnIO installation.\r\n\r\nSaved automatic-live startup is opt-in. Shift bypasses it. Conflicting vendor apps block live access and are never killed. This is not a before-login cooling service.\r\n\r\nDuty % is a controller request, not watts or RPM. PWM/DC electrical switching is not exposed by the verified protocol. BIOS pump control remains unchanged.\r\n\r\nA crash, USB loss or frozen process cannot be made safe by an exit handler. Keep independent firmware/BIOS thermal protection. No driver protection, firmware or flash is modified.\r\n\r\nPhysical acceptance is required for LCD images, RGB colors, wiring and cooling. Automated tests are not certification.\r\n\r\nNo PSU/RAM RGB/cloud/firmware updates. No network server. GPL-3.0-or-later; see LICENSE and THIRD-PARTY-NOTICES.txt."; about.Controls.Add(aboutText);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill }; bottom.Controls.Add(new Label { Text = "Sensor interval (s)", AutoSize = true }); poll.Value = initial.PollSeconds; bottom.Controls.Add(poll); resources.AutoSize = true; resources.ForeColor = Muted; bottom.Controls.Add(resources); shell.Controls.Add(bottom, 0, 4);
        cooling.Checked = initial.CoolingEnabled; wiring.Checked = initial.CoolingWiringConfirmed; rgb.Checked = initial.RgbEnabled; monitorFans.Checked = initial.MonitorFanController; color.Text = initial.RgbColor; serial.Text = initial.ControllerSerial;
        TrackSettingsChanges(cooling); TrackSettingsChanges(wiring); TrackSettingsChanges(rgb); TrackSettingsChanges(monitorFans);
        TrackSettingsChanges(color); TrackSettingsChanges(serial); TrackSettingsChanges(poll); TrackSettingsChanges(panelEditor); TrackSettingsChanges(aioEditor);
        ports.CellValueChanged += (_, _) => QueueSettingsApply();
        panelEditor.SettingsChanged += QueueSettingsApply; aioEditor.SettingsChanged += QueueSettingsApply;
        settingsDelay.Tick += (_, _) => ApplyPendingSettings();
        tray.ContextMenuStrip = new ContextMenuStrip(); tray.ContextMenuStrip.Items.Add("Open", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        tray.ContextMenuStrip.Items.Add("Stop hardware control", null, async (_, _) => await Run(async () => await engine.Stop()));
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => { exiting = true; Close(); }); tray.DoubleClick += (_, _) => { Show(); Activate(); };
        refresh.Tick += (_, _) => { if (Visible && !busy) RefreshData(); WriteAcceptanceStatus(); };
        Shown += (_, _) =>
        {
            if (smokeMode) return;
            try { engine.Start(settings, StartupRequested); refresh.Start(); tray.Visible = true; if (StartMinimized) Hide(); }
            catch (Exception error) { refresh.Start(); tray.Visible = true; Show(); Error(new InvalidOperationException("Startup was blocked safely. No conflicting app was stopped. " + error.Message)); }
        };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && tray.Visible) Hide(); };
        FormClosing += ClosingAsync;
        ApplyColors(this);
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
        ports.DefaultCellStyle.SelectionBackColor = Color.FromArgb(36, 81, 93); ports.EnableHeadersVisualStyles = false;
        ports.ColumnHeadersDefaultCellStyle.BackColor = Background; ports.ColumnHeadersDefaultCellStyle.ForeColor = Muted; ports.RowTemplate.Height = 34;
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "Port", ReadOnly = true, FillWeight = 35 });
        foreach (var pair in new[] { ("Role", Enum.GetNames<PortRole>()), ("Mode", Enum.GetNames<ControlMode>()), ("Source", Enum.GetNames<TemperatureSource>()) })
            ports.Columns.Add(new DataGridViewComboBoxColumn { Name = pair.Item1, DataSource = pair.Item2, FlatStyle = FlatStyle.Flat, FillWeight = 75 });
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "Minimum", HeaderText = "Min %", FillWeight = 42 });
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "Fixed", HeaderText = "Fixed %", FillWeight = 42 });
        ports.Columns.Add(new DataGridViewCheckBoxColumn { Name = "RGB", FillWeight = 36 });
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "LEDs", FillWeight = 36 });
        ports.Columns.Add(new DataGridViewTextBoxColumn { Name = "Curve", HeaderText = "Curve · °C:% separated by ;", FillWeight = 210 });
        ports.Columns.Add(new DataGridViewButtonColumn { Name = "Editor", HeaderText = "Curve / RGB", Text = "Edit…", UseColumnTextForButtonValue = true, FillWeight = 60 });
        foreach (var p in settings.Ports) ports.Rows.Add(p.Port, p.Role.ToString(), p.Mode.ToString(), p.Source.ToString(), p.MinimumDuty, p.FixedDuty, p.Rgb, p.LedCount, string.Join(';', p.Curve.Select(x => $"{x.Temperature.ToString(CultureInfo.InvariantCulture)}:{x.Duty.ToString(CultureInfo.InvariantCulture)}")));
        ports.DataError += (_, e) => { e.ThrowException = false; };
        ports.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 9) return;
            try
            {
                ports.EndEdit(); var row = ports.Rows[e.RowIndex]; using var editor = new PortEditorForm(ReadPort(row), () => engine.Latest, color.Text);
                if (editor.ShowDialog(this) != DialogResult.OK) return;
                var p = editor.Result; portDesign[p.Port] = p.Copy();
                row.SetValues(p.Port, p.Role.ToString(), p.Mode.ToString(), p.Source.ToString(), p.MinimumDuty, p.FixedDuty, p.Rgb, p.LedCount, string.Join(';', p.Curve.Select(x => $"{x.Temperature.ToString(CultureInfo.InvariantCulture)}:{x.Duty.ToString(CultureInfo.InvariantCulture)}")));
            }
            catch (Exception error) { Error(error); }
        };
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
    private Settings ReadSettings()
    {
        ports.EndEdit();
        var s = new Settings { PollSeconds = (int)poll.Value, CoolingEnabled = cooling.Checked, CoolingWiringConfirmed = wiring.Checked, RgbEnabled = rgb.Checked, MonitorFanController = monitorFans.Checked, StartLiveAtSignIn = settings.StartLiveAtSignIn, RgbColor = color.Text.Trim().TrimStart('#'), ControllerSerial = serial.Text.Trim(), Panel = panelEditor.Read(), Aio = aioEditor.Read() };
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
        try { await action(); } catch (Exception e) { Error(e); } finally { busy = false; RefreshData(); }
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
                : "Start live control of the enabled devices? Close TT RGB Plus, OpenRGB, Fan Control and SignalRGB first. No application will be closed automatically.";
            if (next.CoolingEnabled) warning += "\n\nCooling: you must have verified wiring and independent thermal protection. USB disconnection or a crashed/frozen process cannot guarantee a safe fan speed. Do not use experimental cooling unattended.";
            if (next.Panel.Content == DisplayContent.Screen || next.Aio.Content == DisplayContent.Screen) warning += "\n\nScreen mirroring can display private information on the physical LCD.";
            if (MessageBox.Show(warning, "Confirm live hardware access", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            if (updating) { engine.ApplyLiveSettings(next); settings = next; return; }
        }
        await engine.Stop(); settings = next; engine.Start(next, live);
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
    { if (smokeMode || exiting) return; settingsDelay.Stop(); settingsDelay.Start(); }
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
        static string Temp(double? x) => x.HasValue ? $"{x:0.0} °C" : "Unavailable";
        cpu.Text = $"CPU\n{Temp(t.CpuTemperature)}   ·   {t.CpuLoad:0}% load";
        if (engine.Live && !t.CpuTemperature.HasValue && !StartupManager.Elevated) cpu.Text = "CPU\nUnavailable · restart as admin";
        gpu.Text = $"GPU\n{Temp(t.GpuTemperature)}   ·   {t.GpuLoad:0}% load";
        memory.Text = $"MEMORY\n{t.MemoryUsedPercent:0}% used";
        session.Text = engine.Cancelled ? "SESSION CANCELLED · see log · press Stop before reconnecting" : engine.Running ? (engine.Live ? "LIVE · hardware session" : "PREVIEW · simulated data · hardware writes disabled") : "STOPPED";
        session.ForeColor = engine.Live ? Color.FromArgb(255, 194, 100) : Accent;
        liveButton.Text = engine.Running && engine.Live ? "Live · automatic" : "Start live control";
        liveButton.Enabled = !engine.Running || !engine.Live;
        if (engine.ControllerFault) session.Text = "CONTROLLER FAULT · release requested · restore vendor control · see log";
        if (applyHint.Length > 0) session.Text += " · " + applyHint;
        deviceStatus.Text = string.Join(Environment.NewLine + Environment.NewLine, engine.Status.OrderBy(x => x.Key).Select(x => x.Key + "\r\n" + x.Value)) + "\r\n\r\n" + string.Join("\r\n", engine.Fans.Values.OrderBy(x => x.Port).Select(x => $"Port {x.Port}: {x.Rpm} RPM / reported {x.Duty}%"));
        log.Text = string.Join(Environment.NewLine, engine.Logs);
        try { var a = panelRenderer.Render(t, settings.Panel, 480, 128); var old = panelPreview.Image; panelPreview.Image = a; old?.Dispose(); } catch (Exception e) { session.Text += " · panel preview: " + e.Message; }
        try { var a = aioRenderer.Render(t, settings.Aio, 480, 480); var old = aioPreview.Image; aioPreview.Image = a; old?.Dispose(); } catch (Exception e) { session.Text += " · AIO preview: " + e.Message; }
        using var p = Process.GetCurrentProcess();
        var now = DateTime.UtcNow; double elapsed = (now - lastResource).TotalSeconds;
        double percent = elapsed > 0 ? (p.TotalProcessorTime - lastCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100 : 0;
        resources.Text = $"    {(StartupManager.Elevated ? "Administrator" : "Limited user")} · App: {(now < resourceWarmup ? "warming up" : $"{percent:0.00}% CPU")} · {p.WorkingSet64 / 1048576d:0} MiB · {p.Threads.Count} threads · {p.HandleCount} handles";
        lastCpu = p.TotalProcessorTime; lastResource = now;
    }
    private void PickColor() { using var d = new ColorDialog(); if (d.ShowDialog() == DialogResult.OK) color.Text = $"{d.Color.R:X2}{d.Color.G:X2}{d.Color.B:X2}"; }
    private void WriteAcceptanceStatus()
    {
        if (AcceptanceStatusPath is null || DateTime.UtcNow < nextStatusWrite) return;
        nextStatusWrite = DateTime.UtcNow.AddSeconds(5);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AcceptanceStatusPath)!);
            File.WriteAllText(AcceptanceStatusPath, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, engine.Running, engine.Live, engine.Cancelled, administrator = StartupManager.Elevated, telemetry = engine.Latest, fans = engine.Fans.Values.OrderBy(x => x.Port), status = engine.Status, log = engine.Logs.TakeLast(12) }, Settings.JsonOptions));
        }
        catch (Exception error) { session.Text = "Acceptance status output failed: " + error.Message; }
    }
    private async Task RestartElevated() => await Run(async () =>
    {
        if (MessageBox.Show("Restart with administrator rights for CPU sensor access?\n\nThe current hardware session will stop first. The new window opens in Preview; confirm live control again. Windows may show a UAC prompt.", "Restart ThermalDesk", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var next = ReadSettings(); SettingsStore.Save(configPath, next); await engine.Stop();
        var launch = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas", Arguments = $"--wait-for-process {Environment.ProcessId}" };
        using var child = Process.Start(launch) ?? throw new IOException("Elevated restart did not launch.");
        settings = next; exiting = true; BeginInvoke((Action)Close);
    });
    private void ProfileMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Export profile", null, (_, _) => { try { using var d = new SaveFileDialog { Filter = "JSON profile|*.json", FileName = "thermaldesk-profile.json" }; if (d.ShowDialog() == DialogResult.OK) SettingsStore.Save(d.FileName, ReadSettings()); } catch (Exception e) { Error(e); } });
        menu.Items.Add("Import profile", null, (_, _) =>
        {
            try
            {
                using var d = new OpenFileDialog { Filter = "JSON profile|*.json" };
                if (d.ShowDialog() != DialogResult.OK) return;
                var imported = SettingsStore.Load(d.FileName); imported.StartLiveAtSignIn = false; SettingsStore.Save(configPath, imported);
                Info("Profile imported. Close and reopen ThermalDesk to load it. It will start in preview, not live control.");
            }
            catch (Exception e) { Error(e); }
        });
        menu.Show(Cursor.Position);
    }
    private void ConfigureStartup()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(StartupManager.State()).Enabled = false;
        menu.Items.Add("Start preview at sign-in", null, (_, _) =>
        { try { StartupManager.Configure(false); settings.StartLiveAtSignIn = false; SettingsStore.Save(configPath, ReadSettings()); Info("Preview startup registered for this Windows user."); } catch (Exception e) { Error(e); } });
        menu.Items.Add("Start saved LIVE profile at sign-in…", null, (_, _) =>
        {
            try
            {
                var next = ReadSettings(); next.StartLiveAtSignIn = true; next.Validate();
                if (MessageBox.Show("Start the saved live profile automatically after Windows sign-in?\n\nValidate every configured device and independent cooling protection first. The task runs elevated for sensor access. Close/disable vendor auto-start yourself only after hardware tests pass. Conflicts block startup; TT is never killed. This is not a before-login cooling service.\n\nHold Shift during launch to bypass live startup. Missing temperatures still request safe duty on managed channels.", "Confirm automatic live startup", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                var old = settings.Copy(); SettingsStore.Save(configPath, next);
                try { StartupManager.Configure(true); settings = next; Info("Elevated live sign-in task registered. Saved devices only; next sign-in will apply it."); }
                catch { SettingsStore.Save(configPath, old); throw; }
            }
            catch (Exception e) { Error(e); }
        });
        menu.Items.Add("Remove ThermalDesk startup", null, (_, _) =>
        { try { StartupManager.Remove(); settings.StartLiveAtSignIn = false; SettingsStore.Save(configPath, ReadSettings()); Info("ThermalDesk startup removed. Other applications were not changed."); } catch (Exception e) { Error(e); } });
        menu.Closed += (_, _) => menu.Dispose(); menu.Show(Cursor.Position);
    }
    private void ExportDiagnostics()
    {
        using var d = new SaveFileDialog { Filter = "JSON diagnostic report|*.json", FileName = "thermaldesk-diagnostics.json" };
        if (d.ShowDialog() != DialogResult.OK) return;
        File.WriteAllText(d.FileName, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, devices = HidLink.Inventory(), session = engine.Status, telemetry = engine.Latest, fans = engine.Fans, log = engine.Logs }, Settings.JsonOptions));
    }
    private async void ClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (exiting && !engine.Running) return;
        e.Cancel = true;
        if (busy) return;
        busy = true;
        try { await engine.Stop(); exiting = true; refresh.Stop(); settingsDelay.Stop(); settingsDelay.Dispose(); tray.Visible = false; tray.Dispose(); panelRenderer.Dispose(); aioRenderer.Dispose(); panelPreview.Image?.Dispose(); aioPreview.Image?.Dispose(); Close(); }
        catch (Exception error) { Error(error); } finally { busy = false; }
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
        smokeMode = true; poll.Value = 3; ApplyPendingSettings();
        if (SettingsStore.Load(configPath).PollSeconds != 3 || engine.Live) throw new InvalidOperationException("Automatic preview persistence failed or opened hardware.");
        cooling.Checked = true; ApplyPendingSettings();
        if (!applyHint.StartsWith("Pending") || SettingsStore.Load(configPath).CoolingEnabled) throw new InvalidOperationException("Invalid automatic change was persisted.");
        settingsDelay.Stop(); return 2;
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { settingsDelay.Dispose(); refresh.Dispose(); } base.Dispose(disposing); }
}

internal sealed class DisplayEditor : GroupBox
{
    public event Action? SettingsChanged;
    private readonly CheckBox enabled = new() { Text = "Enable this LCD", AutoSize = true }, overlay = new() { Text = "Metrics overlay", AutoSize = true };
    private readonly CheckBox custom = new() { Text = "Use custom widget layout", AutoSize = true };
    private readonly ComboBox content = new() { DropDownStyle = ComboBoxStyle.DropDownList }, rotation = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox path = new();
    private readonly NumericUpDown brightness = new() { Minimum = 0, Maximum = 100 };
    private readonly NumericUpDown frames = new() { Minimum = 1, Maximum = 10 };
    private DisplaySettings design;
    public DisplayEditor(string title, DisplaySettings s, int width, int height, Func<Telemetry> telemetry)
    {
        design = s.Copy();
        Text = title; Dock = DockStyle.Fill; ForeColor = Color.White; Padding = new Padding(16); Margin = new Padding(5);
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 10 };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 115)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); Controls.Add(grid);
        content.Items.AddRange(Enum.GetNames<DisplayContent>()); rotation.Items.AddRange(["0", "90", "180", "270"]);
        enabled.Checked = s.Enabled; overlay.Checked = s.Overlay; custom.Checked = s.UseCustomLayout; content.SelectedItem = s.Content.ToString(); rotation.SelectedItem = s.Rotation.ToString(); brightness.Value = s.Brightness; frames.Value = s.FramesPerSecond; path.Text = s.MediaPath;
        grid.Controls.Add(enabled, 0, 0); grid.SetColumnSpan(enabled, 2);
        var fields = new (string, Control)[] { ("Content", content), ("Media file", path), ("Brightness %", brightness), ("Rotation °", rotation), ("Frames / sec", frames), ("", overlay) };
        for (int i = 0; i < fields.Length; i++) { grid.RowStyles.Add(new(SizeType.Absolute, 42)); grid.Controls.Add(new Label { Text = fields[i].Item1, AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 0, i + 1); fields[i].Item2.Dock = DockStyle.Fill; grid.Controls.Add(fields[i].Item2, 1, i + 1); }
        var browse = new Button { Text = "Choose image / GIF", AutoSize = true, FlatStyle = FlatStyle.Flat };
        browse.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.gif;*.bmp" }; if (d.ShowDialog() == DialogResult.OK) { path.Text = d.FileName; content.SelectedItem = "Image"; } };
        grid.Controls.Add(browse, 1, 7);
        grid.RowStyles.Add(new(SizeType.Absolute, 40)); grid.RowStyles.Add(new(SizeType.Absolute, 40)); grid.RowStyles.Add(new(SizeType.Absolute, 46));
        grid.Controls.Add(custom, 1, 8);
        var edit = new Button { Text = "Edit widgets…", AutoSize = true, FlatStyle = FlatStyle.Flat };
        edit.Click += (_, _) =>
        {
            try
            {
                using var editor = new WidgetDesignerForm(title, Read(), width, height, telemetry);
                if (editor.ShowDialog(FindForm()) == DialogResult.OK) { design = editor.Result; custom.Checked = true; overlay.Checked = true; SettingsChanged?.Invoke(); }
            }
            catch (Exception error) { MessageBox.Show(error.Message, "Widget designer", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        grid.Controls.Add(edit, 1, 9);
        rotation.SelectedIndexChanged += (_, _) =>
        {
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
    }
    public DisplaySettings Read()
    {
        var result = design.Copy(); result.Enabled = enabled.Checked; result.Content = Enum.Parse<DisplayContent>((string)content.SelectedItem!); result.MediaPath = path.Text.Trim(); result.Brightness = (int)brightness.Value; result.FramesPerSecond = (int)frames.Value; result.Rotation = int.Parse((string)rotation.SelectedItem!, CultureInfo.InvariantCulture); result.Overlay = overlay.Checked; result.UseCustomLayout = custom.Checked; return result;
    }
}
