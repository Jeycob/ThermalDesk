using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class SensorSelector : GroupBox
{
    private sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    private readonly ComboBox gpu = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly Label details = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly Func<Telemetry> telemetry;
    private bool syncing;
    private string choicesKey = "";
    public event Action? SettingsChanged;
    public SensorSelector(string selectedId, Func<Telemetry> read)
    {
        telemetry = read; Text = "Telemetry sources"; Dock = DockStyle.Fill; Padding = new Padding(18);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 55)); layout.RowStyles.Add(new(SizeType.Absolute, 42));
        layout.RowStyles.Add(new(SizeType.Absolute, 95)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Select one GPU for core temperature, hotspot, memory temperature and load.\nAutomatic prefers NVIDIA, then AMD, then Intel. A missing pinned device never falls back to another GPU." }, 0, 0);
        layout.Controls.Add(gpu, 0, 1);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "CPU prefers its package / Tctl-Tdie temperature; a named fallback is shown if unavailable.\nRAM percentage is physical memory, not virtual-memory load.\nActive cooling keeps sensor sampling at two seconds or faster.\nLive sensor choices appear after explicitly starting monitoring; Preview does not enumerate hardware." }, 0, 2);
        layout.Controls.Add(details, 0, 3); Controls.Add(layout);
        gpu.Items.Add(new Choice("", "Automatic · prefer NVIDIA"));
        if (selectedId.Length > 0) gpu.Items.Add(new Choice(selectedId, "Saved GPU (not detected yet) · " + selectedId));
        gpu.SelectedIndex = gpu.Items.Count - 1;
        gpu.SelectedIndexChanged += (_, _) => { if (!syncing) SettingsChanged?.Invoke(); };
    }
    public string Read() => (gpu.SelectedItem as Choice)?.Id ?? "";
    public void UpdateTelemetry()
    {
        var t = telemetry(); string selected = Read();
        string key = selected + "\n" + string.Join('\n', t.GpuDevices.Select(d => d.Id + ":" + d.Name));
        if (key != choicesKey && !gpu.DroppedDown)
        {
            syncing = true;
            try
            {
                gpu.Items.Clear(); gpu.Items.Add(new Choice("", "Automatic · prefer NVIDIA"));
                foreach (var d in t.GpuDevices) gpu.Items.Add(new Choice(d.Id, d.Name + " · " + d.Id));
                if (selected.Length > 0 && t.GpuDevices.All(d => d.Id != selected)) gpu.Items.Add(new Choice(selected, "Saved GPU (unavailable) · " + selected));
                gpu.SelectedItem = gpu.Items.Cast<Choice>().Single(c => c.Id == selected); choicesKey = key;
            }
            finally { syncing = false; }
        }
        static string Source(string text) => text.Length == 0 ? "Unavailable / simulated preview" : text;
        details.Text = $"Sampled CPU temperature: {Source(t.CpuTemperatureSource)}\nCPU maximum sensor: {Source(t.CpuMaximumTemperatureSource)} (also checked for real critical heat)\n\nSampled GPU: {Source(t.GpuDeviceName)}\nGPU identifier: {Source(t.GpuDeviceId)}\nGPU core temperature: {Source(t.GpuTemperatureSource)}\n\nRAM load: {Source(t.MemoryLoadSource)}\n\nValues follow the selected device on the next sensor sample. Hotspot and VRAM temperatures are separate from core temperature.";
    }
    internal int ExerciseForTest()
    {
        int checks = 0, events = 0;
        void Verify(bool condition, string name) { checks++; if (!condition) throw new InvalidOperationException("Sensor selector: " + name); }
        SettingsChanged += () => events++;
        string saved = Read(); UpdateTelemetry();
        Verify(Read() == saved && events == 0, "refresh preserves saved selection without applying settings");
        Verify(details.Text.Contains("GPU identifier:") && details.Text.Contains("RAM load:"), "source provenance is visible");
        gpu.SelectedIndex = 0;
        Verify(Read() == "" && events == 1, "selecting automatic is an explicit change");
        UpdateTelemetry(); Verify(Read() == "" && events == 1, "automatic remains automatic after discovery");
        var device = gpu.Items.Cast<Choice>().First(c => c.Id.Length > 0);
        gpu.SelectedItem = device; Verify(Read() == device.Id && events == 2, "specific GPU selection emits one change");
        UpdateTelemetry(); Verify(Read() == device.Id && events == 2, "pinned selection remains stable");
        return checks;
    }
}
