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
    private readonly CheckBox rgb = new() { Text = "Enable RGB on this port", AutoSize = true };
    private readonly TextBox color = new(), portName = new(), palette = new();
    private readonly DataGridView points = new();
    private readonly Label status = new() { AutoSize = true };
    private readonly CurveCanvas chart;
    public PortSettings Result { get; private set; }
    public PortEditorForm(PortSettings settings, Func<Telemetry>? telemetry = null, string fallbackColor = "23D6BA")
    {
        initial = settings.Copy(); Result = settings.Copy(); Text = $"Port {settings.Port} · Cooling and lighting";
        Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(16, 25, 38); ForeColor = Color.White; ClientSize = new(950, 690); MinimumSize = new(900, 650); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 4 };
        root.ColumnStyles.Add(new(SizeType.Percent, 42)); root.ColumnStyles.Add(new(SizeType.Percent, 58)); root.RowStyles.Add(new(SizeType.Absolute, 58)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 105)); root.RowStyles.Add(new(SizeType.Absolute, 48)); Controls.Add(root);
        var warning = new Label { Dock = DockStyle.Fill, ForeColor = Color.Orange, Text = "Changes apply automatically after closing this editor; the controller does not restart.\nDuty % is a request, not watts or RPM. Silent is temperature-controlled, never fan-off. BIOS pump control is unchanged." }; root.Controls.Add(warning, 0, 0); root.SetColumnSpan(warning, 2);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9, AutoScroll = true }; fields.ColumnStyles.Add(new(SizeType.Absolute, 135)); fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        var pairs = new (string, Control)[] { ("Location / name", portName), ("Wiring role", role), ("Control mode", mode), ("Temperature", source), ("Minimum duty %", minimum), ("Fixed duty %", fixedDuty), ("", rgb), ("LED count", leds), ("RGB effect", effect), ("Brightness %", brightness), ("Effect period (s)", period), ("Color hex (or blank)", color), ("Wave direction", direction), ("Palette (comma hex)", palette) };
        for (int i = 0; i < pairs.Length; i++) { fields.RowStyles.Add(new(SizeType.Absolute, 30)); fields.Controls.Add(new Label { Text = pairs[i].Item1, AutoSize = true, Margin = new Padding(0, 4, 0, 0) }, 0, i); pairs[i].Item2.Dock = DockStyle.Fill; fields.Controls.Add(pairs[i].Item2, 1, i); }
        role.SelectedItem = settings.Role.ToString(); mode.SelectedItem = settings.Mode.ToString(); source.SelectedItem = settings.Source.ToString(); effect.SelectedItem = settings.RgbEffect.ToString(); minimum.Value = settings.MinimumDuty; fixedDuty.Value = settings.FixedDuty; leds.Value = settings.LedCount; brightness.Value = settings.RgbBrightness; period.Value = (decimal)settings.RgbPeriodSeconds; rgb.Checked = settings.Rgb; color.Text = settings.RgbColor;
        role.SelectedIndexChanged += (_, _) => { bool pump = (string)role.SelectedItem! == nameof(PortRole.Pump); minimum.Minimum = fixedDuty.Minimum = pump ? 60 : 30; if (minimum.Value < minimum.Minimum) minimum.Value = minimum.Minimum; if (fixedDuty.Value < fixedDuty.Minimum) fixedDuty.Value = fixedDuty.Minimum; };
        portName.Text = settings.Name;
        direction.SelectedItem = settings.RgbDirection.ToString(); palette.Text = string.Join(", ", settings.RgbPalette);
        root.Controls.Add(fields, 0, 1);
        var graphPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        graphPanel.RowStyles.Add(new(SizeType.Absolute, 45)); graphPanel.RowStyles.Add(new(SizeType.Percent, 65)); graphPanel.RowStyles.Add(new(SizeType.Percent, 35));
        points.Dock = DockStyle.Fill; points.AllowUserToAddRows = false; points.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; points.RowHeadersVisible = false; points.BackgroundColor = BackColor;
        points.DefaultCellStyle.BackColor = Color.FromArgb(22, 34, 48); points.DefaultCellStyle.ForeColor = Color.White; points.DefaultCellStyle.SelectionBackColor = Color.FromArgb(35, 80, 94); points.DefaultCellStyle.SelectionForeColor = Color.White; points.EnableHeadersVisualStyles = false; points.ColumnHeadersDefaultCellStyle.BackColor = BackColor; points.ColumnHeadersDefaultCellStyle.ForeColor = Color.Silver;
        points.Columns.Add("Temperature", "Temperature °C"); points.Columns.Add("Duty", "Duty %"); foreach (var p in settings.Curve) points.Rows.Add(p.Temperature, p.Duty);
        chart = new CurveCanvas(ReadPoints, SetPoint);
        graphPanel.Controls.Add(new RgbPreview(Read, telemetry ?? (() => new Telemetry(DateTimeOffset.UtcNow, null, null, null, null, null)), fallbackColor) { Dock = DockStyle.Fill }, 0, 0);
        graphPanel.Controls.Add(chart, 0, 1); graphPanel.Controls.Add(points, 0, 2); root.Controls.Add(graphPanel, 1, 1);
        points.CellValueChanged += (_, _) => chart.Invalidate(); points.RowsRemoved += (_, _) => chart.Invalidate(); points.DataError += (_, e) => { e.ThrowException = false; status.Text = "Enter numeric curve values."; };
        var help = new FlowLayoutPanel { Dock = DockStyle.Fill };
        foreach (var preset in Enum.GetValues<FanPreset>())
            help.Controls.Add(Button(preset.ToString(), () => ApplyPreset(preset)));
        help.Controls.Add(Button("Import TT curve…", ImportTtCurve));
        help.Controls.Add(Button("Add point", () => { points.EndEdit(); var curve = ReadPoints(); if (curve.Count >= 20) throw new ArgumentException("Maximum 20 points."); if (curve.Count == 0) points.Rows.Add(30d, 40d); else { var gap = curve.Zip(curve.Skip(1)).Select((p, i) => (Gap: p.Second.Temperature - p.First.Temperature, Index: i)).OrderByDescending(p => p.Gap).FirstOrDefault(); if (gap.Gap > .2) { var a = curve[gap.Index]; var b = curve[gap.Index + 1]; points.Rows.Insert(gap.Index + 1, (a.Temperature + b.Temperature) / 2, (a.Duty + b.Duty) / 2); } else if (curve[^1].Temperature < 100) points.Rows.Add(100d, 100d); } chart.Invalidate(); }));
        help.Controls.Add(Button("Remove point", () => { if (points.Rows.Count <= 2) throw new ArgumentException("Keep at least two points."); if (points.CurrentCell is { } c) points.Rows.RemoveAt(c.RowIndex); }));
        help.Controls.Add(Button("Pick color…", () => { using var d = new ColorDialog { FullOpen = true, Color = color.Text.Length == 0 ? Color.Turquoise : FrameRenderer.ParseColor(color.Text) }; if (d.ShowDialog(this) == DialogResult.OK) color.Text = $"{d.Color.R:X2}{d.Color.G:X2}{d.Color.B:X2}"; }));
        help.Controls.Add(new Label { AutoSize = true, Text = "Drag curve points. Fan minimum ≥30%, pump minimum ≥60%.\nTT dips are retained. Missing data releases control; real critical heat can request full speed.\nPaletteWave: 2–8 colors, e.g. FF0000, AA00FF. Effects are opt-in, capped at 5 FPS." }); root.Controls.Add(help, 0, 2); root.SetColumnSpan(help, 2);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.Add(Button("Apply to profile", () => { points.EndEdit(); var value = Read(); var test = new Settings(); test.Ports[settings.Port - 1] = value; test.Validate(); Result = value; DialogResult = DialogResult.OK; Close(); })); footer.Controls.Add(Button("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); })); footer.Controls.Add(status); root.Controls.Add(footer, 0, 3); root.SetColumnSpan(footer, 2);
    }
    private PortSettings Read()
    {
        var p = initial.Copy(); p.Name = portName.Text.Trim();
        p.Role = Enum.Parse<PortRole>((string)role.SelectedItem!); p.Mode = Enum.Parse<ControlMode>((string)mode.SelectedItem!);
        p.Source = Enum.Parse<TemperatureSource>((string)source.SelectedItem!); p.MinimumDuty = (int)minimum.Value;
        p.FixedDuty = (int)fixedDuty.Value; p.Curve = ReadPoints(); p.Rgb = rgb.Checked; p.LedCount = (int)leds.Value;
        p.RgbEffect = Enum.Parse<RgbEffect>((string)effect.SelectedItem!); p.RgbBrightness = (int)brightness.Value;
        p.RgbPeriodSeconds = (double)period.Value; p.RgbColor = color.Text.Trim().TrimStart('#');
        p.RgbDirection = Enum.Parse<RgbDirection>((string)direction.SelectedItem!);
        p.RgbPalette = string.IsNullOrWhiteSpace(palette.Text) ? [] : palette.Text.Split(',').Select(c => c.Trim().TrimStart('#')).ToList(); return p;
    }
    private void ApplyPreset(FanPreset preset)
    {
        points.EndEdit(); var p = Read(); FanPresets.Apply(p, preset);
        initial.CustomCurveBackup = p.CustomCurveBackup.ToList();
        mode.SelectedItem = p.Mode.ToString(); minimum.Value = p.MinimumDuty; fixedDuty.Value = p.FixedDuty;
        points.Rows.Clear(); foreach (var point in p.Curve) points.Rows.Add(point.Temperature, point.Duty);
        chart.Invalidate(); status.ForeColor = Color.Turquoise; status.Text = preset + " selected · updates automatically after closing this editor";
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
        rgb.Checked = true; effect.SelectedItem = nameof(RgbEffect.PaletteWave); direction.SelectedItem = nameof(RgbDirection.Reverse); palette.Text = "FF0000, #AA00FF"; color.Text = "123456"; brightness.Value = 45; period.Value = 6;
        SetPoint(1, new(50, 55)); var p = Read(); var check = new Settings(); check.Ports[p.Port - 1] = p; check.Validate();
        Verify(p.Curve[1].Duty == 55 && p.Source == TemperatureSource.Gpu && p.Mode == ControlMode.Curve, "curve/source roundtrip");
        Verify(p.Rgb && p.RgbEffect == RgbEffect.PaletteWave && p.RgbBrightness == 45 && p.RgbPeriodSeconds == 6 && p.RgbColor == "123456", "lighting roundtrip");
        Verify(p.RgbDirection == RgbDirection.Reverse && p.RgbPalette.SequenceEqual(new[] { "FF0000", "AA00FF" }) && initial.RgbPalette.Count == 0, "palette/direction isolated roundtrip");
        ApplyPreset(FanPreset.Full); Verify(Read().Mode == ControlMode.Fixed && Read().FixedDuty == 100, "Full preset");
        ApplyPreset(FanPreset.Silent); Verify(FanPresets.Identify(Read()) == FanPreset.Silent, "Silent preset");
        ApplyPreset(FanPreset.Custom); Verify(Read().Mode == ControlMode.Curve, "Custom editable curve");
        role.SelectedItem = nameof(PortRole.Pump); Verify(minimum.Minimum == 60 && fixedDuty.Minimum == 60 && minimum.Value >= 60 && fixedDuty.Value >= 60, "pump floor controls");
        Verify(initial.Role == PortRole.Unknown && initial.Curve[1].Duty == 50, "edits do not mutate input");
        using var image = new Bitmap(Width, Height); DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png); return count;
    }
    private List<CurvePoint> ReadPoints() => points.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new CurvePoint(Convert.ToDouble(r.Cells[0].Value, CultureInfo.CurrentCulture), Convert.ToDouble(r.Cells[1].Value, CultureInfo.CurrentCulture))).ToList();
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
