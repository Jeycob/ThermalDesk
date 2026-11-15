using System.Globalization;
using ThermalDesk.Core;

namespace ThermalDesk.App;

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
        hints.SetToolTip(cycleSeconds, "Seconds per page (1–3600); 5 is a good start. Independent for each LCD. Choose the editor's Preview page independently from physical page rotation.");
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
