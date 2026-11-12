using System.ComponentModel;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// Native, event-driven pickers. They only edit a profile; no hardware or polling.
internal sealed class RgbColorEditor : UserControl
{
    private readonly string fallback;
    private readonly CheckBox useGlobal = new() { Text = "Use global color", AutoSize = true };
    private readonly TextBox hex = new() { Width = 76, MaxLength = 7 };
    private readonly Button choose = new() { Text = "Choose color…", AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly Label swatch = new() { Width = 30, Height = 27, BorderStyle = BorderStyle.FixedSingle, Margin = new(3) };
    private readonly Label hint = new() { AutoSize = true };
    private readonly ToolTip tips = new() { AutoPopDelay = 15000, InitialDelay = 350, ShowAlways = true };
    private bool changing;
    public event Action? ValueChanged;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UseGlobal { get => useGlobal.Checked; set => useGlobal.Checked = value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value
    {
        get => UseGlobal ? "" : Normalize(hex.Text);
        set
        {
            changing = true;
            try { useGlobal.Checked = string.IsNullOrWhiteSpace(value); hex.Text = useGlobal.Checked ? fallback : Normalize(value); }
            finally { changing = false; }
            UpdateVisual();
        }
    }
    public RgbColorEditor(string value, string globalColor)
    {
        fallback = Normalize(globalColor); DoubleBuffered = true; Height = 155; MinimumSize = new(190, 155);
        choose.BackColor = Color.FromArgb(30, 48, 64); choose.ForeColor = Color.White; hex.BackColor = Color.FromArgb(22, 34, 48); hex.ForeColor = Color.White;
        tips.SetToolTip(useGlobal, "Follow the main window's global RGB color. Click a preset or Choose color to use a separate color on this port.");
        tips.SetToolTip(choose, "Open the standard color picker. Choosing a color automatically disables Use global color.");
        tips.SetToolTip(hex, "Optional exact RGB value: six hexadecimal digits, for example FF9700 (orange). You can use the picker instead.");
        tips.SetToolTip(swatch, "Current color preview. The effect and brightness may alter the final LED output.");
        var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new(0) };
        rows.RowStyles.Add(new(SizeType.Absolute, 27)); rows.RowStyles.Add(new(SizeType.Absolute, 34));
        rows.RowStyles.Add(new(SizeType.Absolute, 28)); rows.RowStyles.Add(new(SizeType.Absolute, 32)); rows.RowStyles.Add(new(SizeType.Percent, 100));
        rows.Controls.Add(useGlobal, 0, 0);
        var picker = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new(0) };
        picker.Controls.AddRange([swatch, choose]); rows.Controls.Add(picker, 0, 1);
        var advanced = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new(0) };
        advanced.Controls.Add(new Label { Text = "Hex #", AutoSize = true, Margin = new(3, 5, 0, 0) }); advanced.Controls.Add(hex);
        var presets = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new(0) };
        foreach (string code in new[] { "23D6BA", "FF9700", "AA00FF", "168CFF", "FF3030", "FFFFFF" })
        {
            var sample = FrameRenderer.ParseColor(code);
            var button = new Button { Width = 24, Height = 24, BackColor = sample, FlatStyle = FlatStyle.Flat, Margin = new(2), AccessibleName = "Select color #" + code, UseVisualStyleBackColor = false };
            tips.SetToolTip(button, "Use " + (code switch { "23D6BA" => "turquoise", "FF9700" => "orange", "AA00FF" => "purple", "168CFF" => "blue", "FF3030" => "red", _ => "white" }) + " on this port (no hex entry needed)");
            button.Click += (_, _) => SetColor(sample); presets.Controls.Add(button);
        }
        rows.Controls.Add(presets, 0, 2); rows.Controls.Add(advanced, 0, 3); rows.Controls.Add(hint, 0, 4); Controls.Add(rows);
        useGlobal.CheckedChanged += (_, _) => { UpdateVisual(); if (!changing) ValueChanged?.Invoke(); };
        hex.TextChanged += (_, _) => { UpdateVisual(); if (!changing) ValueChanged?.Invoke(); };
        choose.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { FullOpen = true, Color = ReadColor() };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK) SetColor(dialog.Color);
        };
        Value = value;
    }
    internal void SetColor(Color value)
    {
        changing = true;
        try { hex.Text = ToHex(value); useGlobal.Checked = false; }
        finally { changing = false; }
        UpdateVisual(); ValueChanged?.Invoke();
    }
    private Color ReadColor()
    {
        try { return FrameRenderer.ParseColor(UseGlobal ? fallback : Normalize(hex.Text)); }
        catch (ArgumentException) { return FrameRenderer.ParseColor(fallback); }
    }
    private void UpdateVisual()
    {
        hex.Enabled = !UseGlobal;
        swatch.BackColor = ReadColor();
        hint.Text = UseGlobal ? $"Global: #{fallback}" : "Override for this port only";
        hint.ForeColor = Color.Silver;
    }
    internal static string Normalize(string value)
    {
        string result = value.Trim().TrimStart('#').ToUpperInvariant();
        if (!WidgetLayout.IsColor(result)) throw new ArgumentException("Choose a color or enter six hexadecimal digits, e.g. 23D6BA.");
        return result;
    }
    internal static string ToHex(Color value) => $"{value.R:X2}{value.G:X2}{value.B:X2}";
    protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
    internal int ExerciseForTest()
    {
        int assertions = 0; void Verify(bool condition, string text) { assertions++; if (!condition) throw new InvalidOperationException("RGB color editor: " + text); }
        Value = ""; Verify(UseGlobal && Value == "" && swatch.BackColor.ToArgb() == FrameRenderer.ParseColor(fallback).ToArgb(), "empty profile follows actual global color");
        SetColor(Color.FromArgb(18, 52, 86)); Verify(!UseGlobal && Value == "123456", "picker explicitly selects per-port override");
        UseGlobal = true; Verify(Value == "", "global checkbox removes override");
        UseGlobal = false; Verify(Value == "123456", "toggling global retains unsaved custom color");
        Value = "#aBcDeF"; Verify(Value == "ABCDEF", "hex normalization");
        bool rejected = false; try { Value = "grey"; } catch (ArgumentException) { rejected = true; }
        Verify(rejected, "invalid colors rejected before applying");
        return assertions;
    }
}

internal sealed class RgbPaletteEditor : UserControl
{
    private readonly List<string> colors = [];
    private readonly FlowLayoutPanel chips = new() { Dock = DockStyle.Fill, WrapContents = true, Margin = new(0) };
    private readonly Button add = new() { Text = "Add", Width = 45, AccessibleName = "Add palette color", FlatStyle = FlatStyle.Flat };
    private readonly Button remove = new() { Text = "Remove", Width = 65, AccessibleName = "Remove selected palette color", FlatStyle = FlatStyle.Flat };
    private readonly Button left = new() { Text = "←", Width = 26, AccessibleName = "Move selected color left", FlatStyle = FlatStyle.Flat };
    private readonly Button right = new() { Text = "→", Width = 26, AccessibleName = "Move selected color right", FlatStyle = FlatStyle.Flat };
    private int selected = -1;
    private readonly ToolTip tips = new() { AutoPopDelay = 15000, InitialDelay = 350, ShowAlways = true };
    public event Action? ValueChanged;
    public List<string> Value => colors.ToList();
    public RgbPaletteEditor(IEnumerable<string> initial)
    {
        DoubleBuffered = true; Height = 193; MinimumSize = new(190, 193);
        var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new(0) };
        rows.RowStyles.Add(new(SizeType.Absolute, 24)); rows.RowStyles.Add(new(SizeType.Percent, 100)); rows.RowStyles.Add(new(SizeType.Absolute, 34));
        rows.Controls.Add(new Label { Text = "Click a color to edit", ForeColor = Color.Silver, Dock = DockStyle.Fill }, 0, 0); rows.Controls.Add(chips, 0, 1);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new(0) }; tools.Controls.AddRange([add, remove, left, right]); rows.Controls.Add(tools, 0, 2); Controls.Add(rows);
        foreach (var button in new[] { add, remove, left, right }) { button.BackColor = Color.FromArgb(30, 48, 64); button.ForeColor = Color.White; }
        tips.SetToolTip(add, "Add a color to the wave. A palette has two to eight colors."); tips.SetToolTip(remove, "Remove the highlighted color. At least two colors must remain.");
        tips.SetToolTip(left, "Move the highlighted color one position earlier."); tips.SetToolTip(right, "Move the highlighted color one position later.");
        add.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { FullOpen = true, Color = Color.Turquoise };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK) AddColor(RgbColorEditor.ToHex(dialog.Color));
        };
        remove.Click += (_, _) => RemoveSelected(); left.Click += (_, _) => MoveSelected(-1); right.Click += (_, _) => MoveSelected(1);
        SetColors(initial);
    }
    internal void SetColors(IEnumerable<string> values)
    {
        var next = values.Select(RgbColorEditor.Normalize).ToList();
        if (next.Count > 8 || next.Count == 1) throw new ArgumentException("A palette must contain 2–8 colors, or be empty when unused.");
        colors.Clear(); colors.AddRange(next); selected = next.Count > 0 ? 0 : -1; RefreshChips();
    }
    internal void AddColor(string hex)
    {
        if (colors.Count >= 8) return;
        // Adding to an empty palette starts a valid two-color gradient.
        colors.Add(RgbColorEditor.Normalize(hex)); if (colors.Count == 1) colors.Add("AA00FF");
        selected = colors.Count - 1; RefreshChips(); ValueChanged?.Invoke();
    }
    internal void RemoveSelected()
    {
        if (colors.Count <= 2 || selected < 0) return;
        colors.RemoveAt(selected); selected = Math.Min(selected, colors.Count - 1); RefreshChips(); ValueChanged?.Invoke();
    }
    internal void MoveSelected(int delta)
    {
        int target = selected + delta;
        if (selected < 0 || target < 0 || target >= colors.Count) return;
        (colors[selected], colors[target]) = (colors[target], colors[selected]); selected = target; RefreshChips(); ValueChanged?.Invoke();
    }
    private void RefreshChips()
    {
        chips.SuspendLayout();
        try
        {
            while (chips.Controls.Count > 0) { var old = chips.Controls[0]; chips.Controls.RemoveAt(0); old.Dispose(); }
            for (int i = 0; i < colors.Count; i++)
            {
                int index = i; var value = FrameRenderer.ParseColor(colors[i]);
                var button = new Button { Text = $"{i + 1}  #{colors[i]}", BackColor = value, ForeColor = value.GetBrightness() < .52 ? Color.White : Color.Black, Width = 100, Height = 29, FlatStyle = FlatStyle.Flat, Margin = new(2), UseVisualStyleBackColor = false };
                button.FlatAppearance.BorderSize = i == selected ? 2 : 1; button.FlatAppearance.BorderColor = i == selected ? Color.White : Color.FromArgb(70, 90, 105);
                tips.SetToolTip(button, "Click to select and edit this color; Cancel keeps its color and leaves it selected for the arrow / Remove buttons.");
                button.Click += (_, _) =>
                {
                    selected = index;
                    using var dialog = new ColorDialog { FullOpen = true, Color = FrameRenderer.ParseColor(colors[index]) };
                    if (dialog.ShowDialog(FindForm()) == DialogResult.OK) { colors[index] = RgbColorEditor.ToHex(dialog.Color); ValueChanged?.Invoke(); }
                    RefreshChips();
                };
                chips.Controls.Add(button);
            }
            add.Enabled = colors.Count < 8; remove.Enabled = colors.Count > 2 && selected >= 0;
            left.Enabled = selected > 0; right.Enabled = selected >= 0 && selected < colors.Count - 1;
        }
        finally { chips.ResumeLayout(); }
    }
    internal int ExerciseForTest()
    {
        int assertions = 0; void Verify(bool condition, string text) { assertions++; if (!condition) throw new InvalidOperationException("RGB palette editor: " + text); }
        SetColors([]); AddColor("#123456"); Verify(Value.SequenceEqual(new[] { "123456", "AA00FF" }), "first add makes valid palette");
        AddColor("00ff00"); Verify(Value.Count == 3 && Value[2] == "00FF00", "add normalized swatch");
        MoveSelected(-1); Verify(Value.SequenceEqual(new[] { "123456", "00FF00", "AA00FF" }), "reorder swatches");
        RemoveSelected(); Verify(Value.SequenceEqual(new[] { "123456", "AA00FF" }), "remove selected swatch");
        RemoveSelected(); Verify(Value.Count == 2, "cannot leave an invalid one-color palette");
        var copy = Value; copy.Clear(); Verify(Value.Count == 2, "returned palette is isolated");
        for (int i = 0; i < 10; i++) AddColor("FFFFFF"); Verify(Value.Count == 8 && chips.Controls.Count == 8, "eight-swatch maximum");
        return assertions;
    }
    protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
}
