using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class RgbScheduleForm : Form
{
    private readonly Settings working;
    private readonly CheckBox enabled = new() { Text = "Enable RGB schedule", AutoSize = true };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false, RowHeadersVisible = false, BackgroundColor = Color.FromArgb(22, 34, 48), BorderStyle = BorderStyle.None };
    public Settings Result { get; private set; }
    public RgbScheduleForm(Settings settings)
    {
        working = settings.Copy(); Result = settings.Copy();
        Text = "RGB schedule · lighting only"; ClientSize = new(780, 470); MinimumSize = new(650, 400);
        Font = new("Segoe UI", 10); BackColor = Color.FromArgb(16, 25, 38); ForeColor = Color.White; StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new(18) };
        layout.RowStyles.Add(new(SizeType.Absolute, 38)); layout.RowStyles.Add(new(SizeType.Absolute, 70));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 48)); Controls.Add(layout);
        enabled.Checked = settings.RgbScheduleEnabled; layout.Controls.Add(enabled, 0, 0);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Color.LightSteelBlue, Text =
            "Local PC time · last matching entry stays active until the next entry, including after restart.\n0% = dark; 100% = your port brightness. Empty color preserves effects; a color selects static lighting.\nOnly already-managed RGB ports change. Global RGB OFF and fan curves always take priority." }, 0, 1);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Time · HH:mm", FillWeight = 80 });
        grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Days", HeaderText = "Days", DataSource = Enum.GetValues<ScheduleDays>() });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Brightness", HeaderText = "Brightness %", FillWeight = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Color", HeaderText = "Static color · optional" });
        grid.DefaultCellStyle.BackColor = Color.FromArgb(22, 34, 48); grid.DefaultCellStyle.ForeColor = Color.White;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(35, 83, 101); grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 48, 64); grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.EnableHeadersVisualStyles = false; grid.GridColor = Color.FromArgb(44, 64, 80); grid.RowTemplate.Height = 32;
        foreach (var entry in settings.RgbScheduleEntries) grid.Rows.Add(entry.Time, entry.Days, entry.BrightnessPercent, entry.Color);
        layout.Controls.Add(grid, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        void Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(30, 48, 64), ForeColor = Color.White, Padding = new(3) };
            button.Click += (_, _) => { try { action(); } catch (ArgumentException error) { MessageBox.Show(this, error.Message, Text); } }; buttons.Controls.Add(button);
        }
        Button("+ Entry", () => { if (grid.Rows.Count < 24) grid.Rows.Add("08:00", ScheduleDays.EveryDay, 100, ""); });
        Button("Night preset", () => { grid.Rows.Clear(); grid.Rows.Add("08:00", ScheduleDays.EveryDay, 100, ""); grid.Rows.Add("22:00", ScheduleDays.EveryDay, 0, ""); });
        Button("Pick color…", () => { if (grid.CurrentRow is not { } row) return; using var picker = new ColorDialog { FullOpen = true }; if (picker.ShowDialog(this) == DialogResult.OK) row.Cells["Color"].Value = $"{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}"; });
        Button("Delete", () => { if (grid.CurrentRow is { } row) grid.Rows.Remove(row); });
        Button("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); });
        Button("Apply", () =>
        {
            grid.EndEdit();
            working.RgbScheduleEnabled = enabled.Checked;
            var entries = new List<RgbScheduleEntry>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (!int.TryParse(Convert.ToString(row.Cells["Brightness"].Value), out int brightness)) throw new ArgumentException("Enter brightness as a whole number from 0 to 100.");
                entries.Add(new() { Time = Convert.ToString(row.Cells["Time"].Value)?.Trim() ?? "", Days = row.Cells["Days"].Value is ScheduleDays days ? days : ScheduleDays.EveryDay,
                    BrightnessPercent = brightness, Color = (Convert.ToString(row.Cells["Color"].Value) ?? "").Trim().TrimStart('#') });
            }
            working.RgbScheduleEntries = entries; RgbSchedule.Validate(working);
            Result = working.Copy(); DialogResult = DialogResult.OK; Close();
        });
        layout.Controls.Add(buttons, 0, 3);
    }
}
