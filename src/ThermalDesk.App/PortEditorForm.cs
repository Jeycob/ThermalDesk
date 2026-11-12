using System.Globalization;
using System.Drawing.Drawing2D;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class PortEditorForm : Form
{
    private readonly PortSettings initial;
    private readonly ComboBox role = Choice<PortRole>(), mode = Choice<ControlMode>(), source = Choice<TemperatureSource>(), effect = Choice<RgbEffect>();
    private readonly ComboBox direction = Choice<RgbDirection>();
    private readonly NumericUpDown minimum = Number(30, 100), fixedDuty = Number(30, 100), leds = Number(1, 62), brightness = Number(0, 100), period = Number(1, 60, 1);
    private readonly CheckBox rgb = new() { Text = "Lighting ON", AutoSize = true };
    private readonly ToolTip tips = new() { AutoPopDelay = 18000, InitialDelay = 350, ReshowDelay = 100, ShowAlways = true };
    private readonly bool rgbMasterEnabled;
    private bool explicitRgbOff;
    private readonly Label fanHint = new() { Dock = DockStyle.Fill, ForeColor = Color.Silver };
    private readonly TextBox portName = new();
    private readonly RgbColorEditor color;
    private readonly RgbPaletteEditor palette;
    private readonly Label lightingHint = new() { AutoSize = false, ForeColor = Color.Silver };
    private readonly TabControl editorTabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView points = new();
    private readonly Label status = new() { AutoSize = true };
    private readonly CurveCanvas chart;
    public PortSettings Result { get; private set; }
    public PortEditorForm(PortSettings settings, Func<Telemetry>? telemetry = null, string fallbackColor = "23D6BA", bool rgbMasterEnabled = true)
    {
        this.rgbMasterEnabled = rgbMasterEnabled; explicitRgbOff = settings.RgbOffRequested;
        initial = settings.Copy(); Result = settings.Copy(); Text = $"Port {settings.Port} · Cooling and lighting";
        color = new(settings.RgbColor, fallbackColor); palette = new(settings.RgbPalette);
        Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(16, 25, 38); ForeColor = Color.White; ClientSize = new(1040, 800); MinimumSize = new(950, 720); StartPosition = FormStartPosition.CenterParent; DoubleBuffered = true;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 4 };
        root.ColumnStyles.Add(new(SizeType.Percent, 42)); root.ColumnStyles.Add(new(SizeType.Percent, 58)); root.RowStyles.Add(new(SizeType.Absolute, 58)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 105)); root.RowStyles.Add(new(SizeType.Absolute, 48)); Controls.Add(root);
        var warning = new Label { Dock = DockStyle.Fill, ForeColor = Color.Orange, Text = "Apply changes updates a running Live session without restarting the controller. Cancel keeps your previous settings.\nDuty % is a request, not watts or RPM. Silent is temperature-controlled, never fan-off. BIOS pump control is unchanged." }; root.Controls.Add(warning, 0, 0); root.SetColumnSpan(warning, 2);
        var fanTab = new TabPage("Fan control") { BackColor = BackColor, ForeColor = ForeColor };
        var lightingTab = new TabPage("RGB lighting") { BackColor = BackColor, ForeColor = ForeColor };
        editorTabs.TabPages.AddRange([fanTab, lightingTab]);
        fanTab.Controls.Add(Fields([( "Location / name", portName, 34), ("Wiring role", role, 34), ("Control mode", mode, 34), ("Temperature", source, 34), ("Minimum duty %", minimum, 34), ("Fixed duty %", fixedDuty, 34), ("", fanHint, 100)]));
        var rgbActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new(0) };
        var turnOff = Button("OFF", () => { explicitRgbOff = true; rgb.Checked = false; RefreshLightingFields(); });
        var leaveUnchanged = Button("Leave unchanged", () => { rgb.Checked = false; explicitRgbOff = false; RefreshLightingFields(); });
        rgbActions.Controls.AddRange([turnOff, leaveUnchanged]);
        tips.SetToolTip(turnOff, "Request black LEDs on this port. It stays OFF after restart while the main RGB switch is enabled. No fan speed changes.");
        tips.SetToolTip(leaveUnchanged, "Do not take over this port's lighting on the next session. If ThermalDesk already lit it, the current session clears it once before releasing lighting writes.");
        tips.SetToolTip(rgb, "ON sends the selected effect; unchecking explicitly requests OFF. Both require Live mode and the main RGB switch. It never switches the fan motor off.");
        lightingTab.Controls.Add(Fields([("Port lighting", rgb, 32), ("", rgbActions, 38), ("LED count", leds, 32), ("RGB effect", effect, 32), ("Brightness %", brightness, 32), ("Effect period (s)", period, 32), ("Port color", color, 158), ("Wave direction", direction, 32), ("Wave palette", palette, 196), ("", lightingHint, 90)]));
        role.SelectedItem = settings.Role.ToString(); mode.SelectedItem = settings.Mode.ToString(); source.SelectedItem = settings.Source.ToString(); effect.SelectedItem = settings.RgbEffect.ToString(); minimum.Value = settings.MinimumDuty; fixedDuty.Value = settings.FixedDuty; leds.Value = settings.LedCount; brightness.Value = settings.RgbBrightness; period.Value = (decimal)settings.RgbPeriodSeconds; rgb.Checked = settings.Rgb;
        role.SelectedIndexChanged += (_, _) => { bool pump = (string)role.SelectedItem! == nameof(PortRole.Pump); minimum.Minimum = fixedDuty.Minimum = pump ? 60 : 30; if (minimum.Value < minimum.Minimum) minimum.Value = minimum.Minimum; if (fixedDuty.Value < fixedDuty.Minimum) fixedDuty.Value = fixedDuty.Minimum; RefreshFanFields(); };
        portName.Text = settings.Name;
        direction.SelectedItem = settings.RgbDirection.ToString();
        effect.SelectedIndexChanged += (_, _) => { if ((string?)effect.SelectedItem == nameof(RgbEffect.PaletteWave) && palette.Value.Count == 0) palette.SetColors([fallbackColor, "AA00FF"]); RefreshLightingFields(); RefreshFanFields(); };
        rgb.CheckedChanged += (_, _) => { explicitRgbOff = !rgb.Checked; RefreshLightingFields(); RefreshFanFields(); }; RefreshLightingFields();
        root.Controls.Add(editorTabs, 0, 1);
        var graphPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        graphPanel.RowStyles.Add(new(SizeType.Absolute, 45)); graphPanel.RowStyles.Add(new(SizeType.Percent, 65)); graphPanel.RowStyles.Add(new(SizeType.Percent, 35));
        points.Dock = DockStyle.Fill; points.AllowUserToAddRows = false; points.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; points.RowHeadersVisible = false; points.BackgroundColor = BackColor;
        points.DefaultCellStyle.BackColor = Color.FromArgb(22, 34, 48); points.DefaultCellStyle.ForeColor = Color.White; points.DefaultCellStyle.SelectionBackColor = Color.FromArgb(35, 80, 94); points.DefaultCellStyle.SelectionForeColor = Color.White; points.EnableHeadersVisualStyles = false; points.ColumnHeadersDefaultCellStyle.BackColor = BackColor; points.ColumnHeadersDefaultCellStyle.ForeColor = Color.Silver;
        points.Columns.Add("Temperature", "Temperature °C"); points.Columns.Add("Duty", "Duty %"); foreach (var p in settings.Curve) points.Rows.Add(p.Temperature, p.Duty);
        chart = new CurveCanvas(ReadPoints, SetPoint);
        graphPanel.Controls.Add(new RgbPreview(Read, telemetry ?? (() => new Telemetry(DateTimeOffset.UtcNow, null, null, null, null, null)), fallbackColor) { Dock = DockStyle.Fill }, 0, 0);
        graphPanel.Controls.Add(chart, 0, 1); graphPanel.Controls.Add(points, 0, 2); root.Controls.Add(graphPanel, 1, 1);
        points.CellValueChanged += (_, _) => chart.Invalidate(); points.RowsRemoved += (_, _) => chart.Invalidate(); points.DataError += (_, e) => { e.ThrowException = false; status.Text = "Enter numeric curve values."; };
        mode.SelectedIndexChanged += (_, _) => RefreshFanFields(); minimum.ValueChanged += (_, _) => RefreshFanFields(); fixedDuty.ValueChanged += (_, _) => RefreshFanFields(); RefreshFanFields();
        tips.SetToolTip(chart, "Drag a turquoise point to set temperature and requested duty. A higher minimum duty overrides lower curve points. No fan writes occur until Apply changes.");
        tips.SetToolTip(points, "Click a cell to enter exact values. Temperatures must increase left to right; duty may rise or fall. Two to twenty points are supported.");
        var help = new FlowLayoutPanel { Dock = DockStyle.Fill };
        foreach (var preset in Enum.GetValues<FanPreset>())
        {
            var presetButton = Button(preset == FanPreset.Full ? "Full · 100%" : preset.ToString(), () => ApplyPreset(preset));
            tips.SetToolTip(presetButton, preset switch { FanPreset.Silent => "Quiet temperature curve. The selected minimum duty still applies; critical heat may override it.", FanPreset.Custom => "Restore your saved custom curve, then drag points or edit numbers.", _ => "Explicitly request constant 100% speed on this port. This is a user-selected mode, never a fallback for missing sensor data." });
            help.Controls.Add(presetButton);
        }
        help.Controls.Add(Button("Import TT curve…", ImportTtCurve));
        help.Controls.Add(Button("Add point", () => { points.EndEdit(); var curve = ReadPoints(); if (curve.Count >= 20) throw new ArgumentException("Maximum 20 points."); if (curve.Count == 0) points.Rows.Add(30d, 40d); else { var gap = curve.Zip(curve.Skip(1)).Select((p, i) => (Gap: p.Second.Temperature - p.First.Temperature, Index: i)).OrderByDescending(p => p.Gap).FirstOrDefault(); if (gap.Gap > .2) { var a = curve[gap.Index]; var b = curve[gap.Index + 1]; points.Rows.Insert(gap.Index + 1, (a.Temperature + b.Temperature) / 2, (a.Duty + b.Duty) / 2); } else if (curve[^1].Temperature < 100) points.Rows.Add(100d, 100d); } chart.Invalidate(); }));
        help.Controls.Add(Button("Remove point", () => { if (points.Rows.Count <= 2) throw new ArgumentException("Keep at least two points."); if (points.CurrentCell is { } c) points.Rows.RemoveAt(c.RowIndex); }));
        help.Controls.Add(new Label { AutoSize = true, Text = "Drag points or edit the table. Hover a label for help. Minimum: fan 30%, pump 60%.\nMissing data releases control; only real critical heat can request full speed. RGB never changes fan speed." }); root.Controls.Add(help, 0, 2); root.SetColumnSpan(help, 2);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.Add(Button("Apply changes", () => { points.EndEdit(); var value = Read(); var test = new Settings(); test.Ports[settings.Port - 1] = value; test.Validate(); Result = value; DialogResult = DialogResult.OK; Close(); })); footer.Controls.Add(Button("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); })); footer.Controls.Add(status); root.Controls.Add(footer, 0, 3); root.SetColumnSpan(footer, 2);
    }
    private PortSettings Read()
    {
        var p = initial.Copy(); p.Name = portName.Text.Trim();
        p.Role = Enum.Parse<PortRole>((string)role.SelectedItem!); p.Mode = Enum.Parse<ControlMode>((string)mode.SelectedItem!);
        p.Source = Enum.Parse<TemperatureSource>((string)source.SelectedItem!); p.MinimumDuty = (int)minimum.Value;
        p.FixedDuty = (int)fixedDuty.Value; p.Curve = ReadPoints(); p.Rgb = rgb.Checked; p.RgbOffRequested = !rgb.Checked && explicitRgbOff; p.LedCount = (int)leds.Value;
        p.RgbEffect = Enum.Parse<RgbEffect>((string)effect.SelectedItem!); p.RgbBrightness = (int)brightness.Value;
        p.RgbPeriodSeconds = (double)period.Value; p.RgbColor = color.Value;
        p.RgbDirection = Enum.Parse<RgbDirection>((string)direction.SelectedItem!);
        p.RgbPalette = palette.Value; return p;
    }
    private TableLayoutPanel Fields((string Name, Control Control, int Height)[] fields)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = fields.Length, Padding = new(3) };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 125)); grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < fields.Length; i++)
        {
            grid.RowStyles.Add(new(SizeType.Absolute, fields[i].Height));
            var label = new Label { Text = fields[i].Name, AutoSize = true, Margin = new(0, 5, 0, 0) };
            string hint = FieldHint(fields[i].Name);
            tips.SetToolTip(label, hint); if (hint.Length > 0) tips.SetToolTip(fields[i].Control, hint);
            fields[i].Control.AccessibleDescription = hint;
            grid.Controls.Add(label, 0, i);
            fields[i].Control.Dock = DockStyle.Fill; grid.Controls.Add(fields[i].Control, 1, i);
        }
        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; host.Controls.Add(grid);
        // Return a one-cell scroll container without allowing the fixed rows to shrink.
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 1, Margin = new(0) };
        outer.RowStyles.Add(new(SizeType.Percent, 100)); outer.ColumnStyles.Add(new(SizeType.Percent, 100));
        outer.Controls.Add(host, 0, 0); return outer;
    }
    private void RefreshLightingFields()
    {
        if (effect.SelectedItem is not string selected) return;
        bool solid = selected is nameof(RgbEffect.Static) or nameof(RgbEffect.Breathing) or nameof(RgbEffect.Blink);
        effect.Enabled = brightness.Enabled = rgb.Checked;
        period.Enabled = rgb.Checked && selected is not (nameof(RgbEffect.Static) or nameof(RgbEffect.Temperature));
        color.Enabled = rgb.Checked && solid; palette.Enabled = rgb.Checked && selected == nameof(RgbEffect.PaletteWave);
        direction.Enabled = rgb.Checked && (selected is nameof(RgbEffect.PaletteWave) or nameof(RgbEffect.RainbowWave));
        string detail = !rgb.Checked ? explicitRgbOff ? "OFF selected: sends black LEDs, including at next Live start. The fan keeps running." : "Unchanged: this port's lighting is not selected. Use OFF to explicitly darken it." : solid ? "Port color affects this effect. Global follows the main color; override affects this port only." : selected == nameof(RgbEffect.PaletteWave) ? "Palette colors run in order. Select a swatch to change it; arrows reorder it." : selected == nameof(RgbEffect.Temperature) ? "Colors follow the selected temperature, not the chosen port color." : "This effect generates its own colors. Port color is unused.";
        lightingHint.Text = (rgbMasterEnabled ? "Apply changes updates a running Live session. " : "Main RGB switch is OFF: enable it on the main window for hardware output. ") + detail;
    }
    private void RefreshFanFields()
    {
        if (mode.SelectedItem is not string selected) return;
        bool curve = selected == nameof(ControlMode.Curve), fixedMode = selected == nameof(ControlMode.Fixed);
        source.Enabled = curve || rgb.Checked && (string?)effect.SelectedItem == nameof(RgbEffect.Temperature); minimum.Enabled = curve; fixedDuty.Enabled = fixedMode;
        points.Enabled = curve;
        fanHint.Text = selected == nameof(ControlMode.Preserve) ? "Leave fan speed untouched. The BIOS/vendor setting remains responsible. RGB can still be controlled separately."
            : fixedMode ? $"Constant requested speed: {fixedDuty.Value}%. Temperature safety remains active. Choose Silent for automatic, quieter cooling."
            : $"Temperature-controlled curve. Minimum {minimum.Value}% overrides any lower curve point. Changes are smoothed to avoid rapid fan-speed cycling.";
    }
    private static string FieldHint(string name) => name switch
    {
        "Location / name" => "Your label for this physical controller port, for example Front intake. It does not change wiring.",
        "Wiring role" => "Select Fan only for a verified fan port. Do not change the BIOS-controlled pump. Changing wiring while Live requires Stop.",
        "Control mode" => "Preserve: no speed commands. Fixed: constant duty. Curve: speed follows the selected temperature. Silent/Custom/Full buttons set these for you.",
        "Temperature" => "CPU, selected GPU, or the hotter of both. Missing required sensor data never forces full speed by itself.",
        "Minimum duty %" => "Lower limit for the curve, not a target RPM. Lower curve points are clamped to this value. At least 30% for fans and 60% for pumps.",
        "Fixed duty %" => "Constant requested speed in Fixed mode. This is percent duty, not RPM or watts. 100% is maximum speed and may be loud.",
        "Port lighting" => "ON applies an effect. Unchecking explicitly requests OFF. Live mode and the main RGB switch must be enabled. Fan speed is unaffected.",
        "LED count" => "Number of individually addressable LEDs in the device on this port (1–62), NOT number of fans. Check the fan/device specification. Too few can leave part dark; wave spacing changes with this count. This is not autodetected.",
        "RGB effect" => "Static uses one color. Breathing/Blink vary brightness. Cycle/Rainbow generate colors. Palette uses your swatches. Temperature uses the fan temperature source.",
        "Brightness %" => "Scales LED intensity. 0% is black, 100% full LED brightness. It does not affect fan motor speed.",
        "Effect period (s)" => "Seconds for one complete animated effect cycle. Larger = slower. Not used for Static or Temperature effects.",
        "Port color" => "Pick a color or a quick swatch. Use global color follows the main window's RGB color. Hex is optional; generated-color effects ignore this field.",
        "Wave direction" => "Forward or reverse travel for Rainbow/Palette waves. It does not reverse the fan motor.",
        "Wave palette" => "Two to eight colors. Click a swatch to edit; arrows reorder the selected color. Used only by PaletteWave.",
        _ => ""
    };
    private void ApplyPreset(FanPreset preset)
    {
        points.EndEdit(); var p = Read(); FanPresets.Apply(p, preset);
        initial.CustomCurveBackup = p.CustomCurveBackup.ToList();
        mode.SelectedItem = p.Mode.ToString(); minimum.Value = p.MinimumDuty; fixedDuty.Value = p.FixedDuty;
        points.Rows.Clear(); foreach (var point in p.Curve) points.Rows.Add(point.Temperature, point.Duty);
        chart.Invalidate(); RefreshFanFields(); status.ForeColor = Color.Turquoise; status.Text = preset + " selected · click Apply changes";
    }
    private void ImportTtCurve()
    {
        using var dialog = new Form { Text = "Import TT custom curve", ClientSize = new Size(580, 150), Font = Font, StartPosition = FormStartPosition.CenterParent };
        var input = new TextBox { Dock = DockStyle.Top, Text = "30,30,33,30,30,42,59,100,100" };
        dialog.Controls.Add(new Label { Dock = DockStyle.Top, Height = 55, Text = "Paste nine duty percentages for 20,30,…,100 °C. Dips are retained.\nSource/role are unchanged; safety limits still override the curve." });
        dialog.Controls.Add(input); input.BringToFront();
        var accept = new Button { Text = "Import to profile", Dock = DockStyle.Bottom, Height = 35 };
        accept.Click += (_, _) =>
        {
            try { var curve = TtCurveImporter.Parse(input.Text); points.Rows.Clear(); foreach (var p in curve) points.Rows.Add(p.Temperature, p.Duty); mode.SelectedItem = nameof(ControlMode.Curve); chart.Invalidate(); dialog.DialogResult = DialogResult.OK; }
            catch (ArgumentException e) { MessageBox.Show(dialog, e.Message, "Invalid curve"); }
        };
        dialog.Controls.Add(accept); dialog.ShowDialog(this);
    }
    internal int ExerciseForTest(string screenshot)
    {
        int count = 0; void Verify(bool condition, string name) { count++; if (!condition) throw new InvalidOperationException("Port editor: " + name); }
        StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000); ShowInTaskbar = false; Show(); PerformLayout(); Application.DoEvents();
        role.SelectedItem = nameof(PortRole.Fan); mode.SelectedItem = nameof(ControlMode.Curve); source.SelectedItem = nameof(TemperatureSource.Gpu);
        rgb.Checked = true; effect.SelectedItem = nameof(RgbEffect.PaletteWave); direction.SelectedItem = nameof(RgbDirection.Reverse); palette.SetColors(["FF0000", "#AA00FF"]); color.Value = "123456"; brightness.Value = 45; period.Value = 6;
        SetPoint(1, new(50, 55)); var p = Read(); var check = new Settings(); check.Ports[p.Port - 1] = p; check.Validate();
        Verify(p.Curve[1].Duty == 55 && p.Source == TemperatureSource.Gpu && p.Mode == ControlMode.Curve, "curve/source roundtrip");
        Verify(p.Rgb && p.RgbEffect == RgbEffect.PaletteWave && p.RgbBrightness == 45 && p.RgbPeriodSeconds == 6 && p.RgbColor == "123456", "lighting roundtrip");
        Verify(p.RgbDirection == RgbDirection.Reverse && p.RgbPalette.SequenceEqual(new[] { "FF0000", "AA00FF" }) && initial.RgbPalette.Count == 0, "palette/direction isolated roundtrip");
        Verify(!color.Enabled && palette.Enabled && direction.Enabled, "only effect-relevant RGB controls enabled");
        effect.SelectedItem = nameof(RgbEffect.Static); Verify(color.Enabled && !palette.Enabled && !direction.Enabled, "static uses visible port color picker");
        color.UseGlobal = true; Verify(Read().RgbColor == "", "global color preserved as empty override in profile");
        color.SetColor(Color.Coral); Verify(Read().RgbColor == "FF7F50", "native color selection roundtrip");
        rgb.Checked = false; Verify(!color.Enabled && !palette.Enabled && Read().RgbOffRequested && !period.Enabled, "RGB OFF explicitly persists black output and disables irrelevant controls"); rgb.Checked = true;
        Verify(!Read().RgbOffRequested, "ON clears persisted OFF");
        Verify(tips.GetToolTip(leds)?.Contains("NOT number of fans") == true && tips.GetToolTip(rgb)?.Contains("OFF") == true, "LED count and ON/OFF have useful hover hints");
        count += color.ExerciseForTest(); count += palette.ExerciseForTest();
        color.Value = "123456"; palette.SetColors(["FF0000", "AA00FF"]);
        ApplyPreset(FanPreset.Full); Verify(Read().Mode == ControlMode.Fixed && Read().FixedDuty == 100, "Full preset");
        ApplyPreset(FanPreset.Silent); Verify(FanPresets.Identify(Read()) == FanPreset.Silent, "Silent preset");
        ApplyPreset(FanPreset.Custom); Verify(Read().Mode == ControlMode.Curve, "Custom editable curve");
        role.SelectedItem = nameof(PortRole.Pump); Verify(minimum.Minimum == 60 && fixedDuty.Minimum == 60 && minimum.Value >= 60 && fixedDuty.Value >= 60, "pump floor controls");
        Verify(initial.Role == PortRole.Unknown && initial.Curve[1].Duty == 50, "edits do not mutate input");
        using (var image = new Bitmap(Width, Height)) { DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png); }
        ClientSize = new(950, 720); editorTabs.SelectedIndex = 1; PerformLayout(); Application.DoEvents();
        var host = (Panel)palette.Parent!.Parent!;
        host.ScrollControlIntoView(palette); Application.DoEvents();
        var viewport = host.RectangleToScreen(host.ClientRectangle);
        Verify(host.AutoScroll && Rectangle.Intersect(viewport, palette.RectangleToScreen(palette.ClientRectangle)).Height >= palette.Height - 2, "palette remains fully accessible at minimum window height");
        host.ScrollControlIntoView(color); Application.DoEvents(); viewport = host.RectangleToScreen(host.ClientRectangle);
        Verify(Rectangle.Intersect(viewport, color.RectangleToScreen(color.ClientRectangle)).Height >= color.Height - 2, "color picker remains fully accessible at minimum window height");
        using (var image = new Bitmap(Width, Height)) { DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-rgb.png"), System.Drawing.Imaging.ImageFormat.Png); }
        return count;
    }
    private List<CurvePoint> ReadPoints() => points.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new CurvePoint(Convert.ToDouble(r.Cells[0].Value, CultureInfo.CurrentCulture), Convert.ToDouble(r.Cells[1].Value, CultureInfo.CurrentCulture))).ToList();
    protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
    private void SetPoint(int index, CurvePoint value) { points.Rows[index].Cells[0].Value = Math.Round(value.Temperature, 1); points.Rows[index].Cells[1].Value = Math.Round(value.Duty); chart.Invalidate(); }
    private static ComboBox Choice<T>() where T : struct, Enum { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; c.Items.AddRange(Enum.GetNames<T>()); return c; }
    private static NumericUpDown Number(int min, int max, int decimals = 0) => new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals > 0 ? .5m : 1 };
    private Button Button(string text, Action action) { var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(30, 48, 64), ForeColor = Color.White }; b.Click += (_, _) => { try { action(); } catch (Exception e) { status.Text = e.Message; status.ForeColor = Color.Orange; } }; return b; }
}

internal sealed class CurveCanvas(Func<List<CurvePoint>> read, Action<int, CurvePoint> update) : Control
{
    private int selected = -1;
    private RectangleF Plot => new(48, 22, Math.Max(1, ClientSize.Width - 68), Math.Max(1, ClientSize.Height - 70));
    private PointF View(CurvePoint p) { var a = Plot; return new(a.Left + (float)p.Temperature / 100 * a.Width, a.Bottom - (float)(p.Duty - 30) / 70 * a.Height); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.Clear(Color.FromArgb(10, 17, 29)); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; var a = Plot;
        using var grid = new Pen(Color.FromArgb(45, 65, 82)); using var pen = new Pen(Color.Turquoise, 2); using var font = new Font("Segoe UI", 9);
        for (int x = 0; x <= 100; x += 20) { float px = View(new(x, 30)).X; e.Graphics.DrawLine(grid, px, a.Top, px, a.Bottom); e.Graphics.DrawString(x.ToString(), font, Brushes.Silver, px - 8, a.Bottom + 3); }
        for (int y = 30; y <= 100; y += 10) { float py = View(new(0, y)).Y; e.Graphics.DrawLine(grid, a.Left, py, a.Right, py); e.Graphics.DrawString(y.ToString(), font, Brushes.Silver, 15, py - 7); }
        e.Graphics.DrawString("Duty %", font, Brushes.Silver, 5, 2); e.Graphics.DrawString("Temperature °C", font, Brushes.Silver, a.Left + a.Width / 2 - 45, a.Bottom + 23);
        try { var p = read().Where(p => double.IsFinite(p.Temperature) && double.IsFinite(p.Duty)).Select(View).ToArray(); if (p.Length > 1) e.Graphics.DrawLines(pen, p); foreach (var dot in p) e.Graphics.FillEllipse(Brushes.Turquoise, dot.X - 5, dot.Y - 5, 10, 10); } catch (Exception) { }
    }
    protected override void OnCreateControl() { base.OnCreateControl(); Dock = DockStyle.Fill; DoubleBuffered = true; }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if(e.Button != MouseButtons.Left)return; try { var p = read(); selected = Enumerable.Range(0, p.Count).FirstOrDefault(i => Math.Abs(View(p[i]).X - e.X) < 12 && Math.Abs(View(p[i]).Y - e.Y) < 12, -1); Capture = selected >= 0; } catch (Exception) { selected = -1; } }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (selected < 0) return; var a = Plot;
        try { var p = read(); if (selected >= p.Count) return; double lo = selected == 0 ? 0 : p[selected - 1].Temperature + .1, hi = selected == p.Count - 1 ? 100 : p[selected + 1].Temperature - .1; if (lo > hi) return; update(selected, new(Math.Clamp((e.X - a.Left) / a.Width * 100, lo, hi), Math.Clamp(30 + (a.Bottom - e.Y) / a.Height * 70, 30, 100))); } catch (Exception) { }
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); selected = -1; Capture = false; }
}
