using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class SensorSelector : GroupBox
{
    private sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    private readonly ComboBox gpu = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
    private readonly CheckBox monitoringOnly = new() { Text = "Allow monitoring-only Live sessions (no USB device required)", AutoSize = true };
    public bool AllowMonitoringOnly => monitoringOnly.Checked;
    private readonly Func<Telemetry> telemetry;
    private bool syncing;
    private string choicesKey = "";
    public event Action? SettingsChanged;
    public SensorSelector(string selectedId, Func<Telemetry> read, bool allowMonitoringOnly = false)
    {
        telemetry = read; Text = "Telemetry sources"; Dock = DockStyle.Fill; Padding = new Padding(18);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 55)); layout.RowStyles.Add(new(SizeType.Absolute, 42));
        layout.RowStyles.Add(new(SizeType.Absolute, 95)); layout.RowStyles.Add(new(SizeType.Absolute, 35)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Intel / AMD CPU · Intel / AMD / NVIDIA GPU, subject to hardware/driver support.\nAutomatic selection is vendor-neutral: dedicated capacity, then stable ID; known iGPUs rank last. Pin a device to choose explicitly." }, 0, 0);
        layout.Controls.Add(gpu, 0, 1);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "CPU prefers its package / Tctl-Tdie temperature; a named fallback is shown if unavailable.\nRAM percentage is physical memory, not virtual-memory load.\nActive cooling keeps sensor sampling at two seconds or faster.\nLive sensor choices appear after explicitly starting monitoring; Preview does not enumerate hardware." }, 0, 2);
        monitoringOnly.Checked = allowMonitoringOnly; layout.Controls.Add(monitoringOnly, 0, 3);
        monitoringOnly.CheckedChanged += (_, _) => { if (!syncing) SettingsChanged?.Invoke(); };
        layout.Controls.Add(details, 0, 4); Controls.Add(layout);
        gpu.Items.Add(new Choice("", "Automatic · vendor-neutral"));
        if (selectedId.Length > 0) gpu.Items.Add(new Choice(selectedId, "Saved GPU (not detected yet) · " + selectedId));
        gpu.SelectedIndex = gpu.Items.Count - 1;
        gpu.SelectedIndexChanged += (_, _) => { if (!syncing) SettingsChanged?.Invoke(); };
    }
    public string Read() => (gpu.SelectedItem as Choice)?.Id ?? "";
    public void LoadSelection(string id, bool? allowMonitoringOnly = null)
    {
        syncing = true;
        try
        {
            var choice = gpu.Items.Cast<Choice>().FirstOrDefault(c => c.Id == id);
            if (choice is null) { choice = new(id, "Saved GPU (not detected yet) · " + id); gpu.Items.Add(choice); }
            gpu.SelectedItem = choice; choicesKey = ""; if (allowMonitoringOnly.HasValue) monitoringOnly.Checked = allowMonitoringOnly.Value;
        }
        finally { syncing = false; }
    }
    public void UpdateTelemetry()
    {
        var t = telemetry(); string selected = Read();
        string key = selected + "\n" + string.Join('\n', t.GpuDevices.Select(d => d.Id + ":" + d.Name));
        if (key != choicesKey && !gpu.DroppedDown)
        {
            syncing = true;
            try
            {
                gpu.Items.Clear(); gpu.Items.Add(new Choice("", "Automatic · vendor-neutral"));
                foreach (var d in t.GpuDevices) gpu.Items.Add(new Choice(d.Id, d.Name + " · " + d.Kind + " · " + d.Id));
                if (selected.Length > 0 && t.GpuDevices.All(d => d.Id != selected)) gpu.Items.Add(new Choice(selected, "Saved GPU (unavailable) · " + selected));
                gpu.SelectedItem = gpu.Items.Cast<Choice>().Single(c => c.Id == selected); choicesKey = key;
            }
            finally { syncing = false; }
        }
        static string Source(string text) => text.Length == 0 ? "Unavailable / simulated preview" : text;
        static string Number(double? value, string format = "0") => value is { } number && double.IsFinite(number) ? number.ToString(format) : "—";
        string text = $"CPU: {Source(t.CpuDeviceName)}\r\nSampled CPU temperature: {Source(t.CpuTemperatureSource)}\r\nCPU maximum sensor: {Source(t.CpuMaximumTemperatureSource)} (also checked for real critical heat)\r\nCPU load: {Source(t.CpuLoadSource)}\r\nCPU sampled core clocks: average {Number(t.CpuClockAverageMhz)} / maximum {Number(t.CpuClockMaximumMhz)} MHz\r\n\r\nSampled GPU: {Source(t.GpuDeviceName)}\r\nGPU identifier: {Source(t.GpuDeviceId)}\r\nGPU core temperature: {Source(t.GpuTemperatureSource)}\r\nGPU load: {Source(t.GpuLoadSource)}\r\nGPU clocks: core {Number(t.GpuCoreClockMhz)} / memory {Number(t.GpuMemoryClockMhz)} MHz\r\nGPU dedicated allocation: {Number(t.GpuVramUsedGiB, "0.0")} / {Number(t.GpuVramTotalGiB, "0.0")} GiB ({Number(t.GpuVramUsedPercent)}%)\r\nGPU shared RAM: {Number(t.GpuSharedMemoryUsedGiB, "0.0")} / {Number(t.GpuSharedMemoryTotalGiB, "0.0")} GiB\r\nMemory source: {t.GpuMemorySource}\r\nRAM load: {Source(t.MemoryLoadSource)}\r\n\r\nDedicated allocation on an iGPU can be reserved system RAM, not separate VRAM chips. Shared RAM is never counted as dedicated capacity.\r\nAutomatic GPU selection stays locked for this Live session. Missing selected hardware stays unavailable; it is not silently replaced.\r\nMonitoring-only does not disable other selected outputs. For read-only use, disable LCDs, RGB and controller monitoring/control.\r\nClocks are reported MHz, not effective clocks or GDDR transfer rate. A dash means the driver did not expose a sensor.";
        text += $"\r\n\r\nCPU power: {Number(t.CpuPowerWatts, "0.0")} W · {t.CpuPowerSource}\r\nGPU power: {Number(t.GpuPowerWatts, "0.0")} W · {t.GpuPowerSource}\r\nWhole PC EST: {Number(t.EstimatedTotalPowerWatts, "0.0")} W\r\nLast-hour averages: CPU {Number(t.CpuPowerHour.Watts, "0.0")} W ({t.CpuPowerHour.Coverage} covered), GPU {Number(t.GpuPowerHour.Watts, "0.0")} W ({t.GpuPowerHour.Coverage}), PC EST {Number(t.TotalPowerHour.Watts, "0.0")} W ({t.TotalPowerHour.Coverage}).\r\n{t.PowerEstimateStatus}";
        text += $"\r\nToday ({t.EnergyToday.Day}): CPU {Number(t.EnergyToday.CpuKwh, "0.000")} kWh, GPU {Number(t.EnergyToday.GpuKwh, "0.000")} kWh, PC EST {Number(t.EnergyToday.TotalKwh, "0.000")} kWh.";
        text += $"\r\nLast 24h accumulated: CPU {Number(t.Energy24h.CpuKwh, "0.000")} kWh, GPU {Number(t.Energy24h.GpuKwh, "0.000")} kWh, whole PC approx. {Number(t.Energy24h.TotalKwh, "0.000")} kWh.\r\nObserved coverage (last 24h): CPU {new PowerAverage(null, t.Energy24h.CpuSeconds).Coverage}, GPU {new PowerAverage(null, t.Energy24h.GpuSeconds).Coverage}, total {new PowerAverage(null, t.Energy24h.TotalSeconds).Coverage}. Actual accumulated estimates, NOT watts x 24. Missing/offline intervals are excluded; totals are partial until 24h is covered. Oldest minute is prorated; whole-PC power is approximate, not a wall meter.";
        if (details.Text != text) details.Text = text;
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
