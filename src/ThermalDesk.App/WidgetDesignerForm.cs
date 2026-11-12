using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal sealed class WidgetDesignerForm : Form
{
    private DisplaySettings working, valid;
    private readonly int physicalWidth, physicalHeight, canvasWidth, canvasHeight;
    private readonly Func<Telemetry> telemetry;
    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly PropertyGrid properties = new() { Dock = DockStyle.Fill, HelpVisible = true, ToolbarVisible = false };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly DesignerCanvas canvas;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private bool rebinding;
    public DisplaySettings Result => working.Copy();

    public WidgetDesignerForm(string title, DisplaySettings settings, int width, int height, Func<Telemetry> getTelemetry)
    {
        working = settings.Copy(); physicalWidth = width; physicalHeight = height; telemetry = getTelemetry;
        (canvasWidth, canvasHeight) = WidgetLayout.Canvas(working, width, height);
        if (!working.UseCustomLayout && working.Widgets.Count == 0) working.Widgets = WidgetLayout.Defaults(canvasWidth, canvasHeight);
        working.UseCustomLayout = true; valid = working.Copy();
        Text = title + " · Widget designer"; Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(16, 25, 38); ForeColor = Color.White;
        ClientSize = new Size(1120, 740); MinimumSize = new Size(1000, 680); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        root.RowStyles.Add(new(SizeType.Absolute, 42)); root.RowStyles.Add(new(SizeType.Absolute, 42)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 52)); Controls.Add(root);
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = $"{canvasWidth} × {canvasHeight} design pixels · drag to move · bottom-right handle to resize · arrow keys = 1 px (Shift = 10 px)\nDesign before rotation. The circle guide marks the round LCD crop. Editing never writes to USB.", ForeColor = Color.FromArgb(151, 172, 191) }, 0, 0);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        tools.Controls.Add(MakeButton("Add", Add)); tools.Controls.Add(MakeButton("Duplicate", Duplicate)); tools.Controls.Add(MakeButton("Delete", Delete));
        tools.Controls.Add(MakeButton("Back", () => Reorder(-1))); tools.Controls.Add(MakeButton("Front", () => Reorder(1)));
        tools.Controls.Add(MakeButton("Font…", PickFont)); tools.Controls.Add(MakeButton("Canvas color…", PickBackground)); tools.Controls.Add(MakeButton("Defaults", Defaults)); tools.Controls.Add(MakeButton("Export PNG…", Export)); root.Controls.Add(tools, 0, 1);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        columns.ColumnStyles.Add(new(SizeType.Absolute, 170)); columns.ColumnStyles.Add(new(SizeType.Percent, 100)); columns.ColumnStyles.Add(new(SizeType.Absolute, 315)); root.Controls.Add(columns, 0, 2);
        list.BackColor = Color.FromArgb(22, 34, 48); list.ForeColor = Color.White; list.BorderStyle = BorderStyle.None;
        columns.Controls.Add(list, 0, 0);
        canvas = new DesignerCanvas(() => working, () => Selected, telemetry, canvasWidth, canvasHeight, width == height);
        columns.Controls.Add(canvas, 1, 0); columns.Controls.Add(properties, 2, 0);
        properties.ViewBackColor = Color.FromArgb(22, 34, 48); properties.ViewForeColor = Color.White; properties.HelpBackColor = BackColor; properties.HelpForeColor = Color.White; properties.CategoryForeColor = Color.FromArgb(38, 218, 179);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 }; footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 110)); footer.ColumnStyles.Add(new(SizeType.Absolute, 110)); footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(MakeButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }), 1, 0);
        var apply = MakeButton("Apply layout", () => { if (Commit()) { DialogResult = DialogResult.OK; Close(); } }); footer.Controls.Add(apply, 2, 0); root.Controls.Add(footer, 0, 3);
        list.SelectedIndexChanged += (_, _) => { if (!rebinding) { properties.SelectedObject = Selected is { } item ? new WidgetProperties(item) : null; canvas.RefreshFrame(); } };
        properties.PropertyValueChanged += (_, _) => Commit();
        canvas.SelectionRequested += widget => { list.SelectedItem = widget; };
        canvas.GeometryChanged += () => { Commit(); properties.Refresh(); };
        timer.Tick += (_, _) => { if (Visible) canvas.RefreshFrame(); };
        Shown += (_, _) => timer.Start();
        Rebind(working.Widgets.FirstOrDefault()?.Id); Commit();
    }
    private LcdWidget? Selected => list.SelectedItem as LcdWidget;
    private static Button MakeButton(string title, Action action)
    {
        var button = new Button { Text = title, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(30, 48, 64), ForeColor = Color.White, Margin = new Padding(3), Padding = new Padding(4) };
        button.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(e.Message, "Widget designer", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }; return button;
    }
    private void Rebind(string? id)
    {
        rebinding = true;
        try { list.Items.Clear(); list.Items.AddRange(working.Widgets.Cast<object>().ToArray()); list.SelectedItem = working.Widgets.FirstOrDefault(w => w.Id == id); properties.SelectedObject = Selected is { } item ? new WidgetProperties(item) : null; }
        finally { rebinding = false; }
        canvas.RefreshFrame();
    }
    private bool Commit()
    {
        string? selectedId = Selected?.Id;
        try { WidgetLayout.Validate(working, physicalWidth, physicalHeight); valid = working.Copy(); status.ForeColor = Color.FromArgb(38, 218, 179); status.Text = $"{working.Widgets.Count}/40 widgets · Preview until Apply layout · then saved/applied automatically."; Rebind(selectedId); return true; }
        catch (ArgumentException e) { working = valid.Copy(); Rebind(selectedId); status.ForeColor = Color.Orange; status.Text = e.Message; return false; }
    }
    private void Add()
    {
        if (working.Widgets.Count >= 40) throw new ArgumentException("Maximum 40 widgets per display.");
        var w = new LcdWidget { Name = "Widget " + (working.Widgets.Count + 1), X = 12, Y = Math.Min(20 + working.Widgets.Count * 10, canvasHeight - 44), Width = Math.Min(220, canvasWidth - 24), Height = 44, FontSize = canvasWidth < 240 ? 18 : 28 };
        working.Widgets.Add(w); Rebind(w.Id); Commit();
    }
    private void Duplicate()
    {
        if (Selected is not { } selected) return;
        if (working.Widgets.Count >= 40) throw new ArgumentException("Maximum 40 widgets per display.");
        var copy = working.Copy().Widgets.Single(w => w.Id == selected.Id); copy.Id = Guid.NewGuid().ToString("N"); copy.Name = (copy.Name.Length > 70 ? copy.Name[..70] : copy.Name) + " copy"; copy.X = Math.Min(canvasWidth - copy.Width, copy.X + 8); copy.Y = Math.Min(canvasHeight - copy.Height, copy.Y + 8); working.Widgets.Add(copy); Rebind(copy.Id); Commit();
    }
    private void Delete() { if (Selected is not { } item) return; int i = list.SelectedIndex; working.Widgets.Remove(item); Rebind(working.Widgets.ElementAtOrDefault(Math.Max(0, i - 1))?.Id); Commit(); }
    private void Reorder(int delta)
    {
        if (Selected is not { } item) return;
        int index = working.Widgets.IndexOf(item), next = Math.Clamp(index + delta, 0, working.Widgets.Count - 1);
        working.Widgets.RemoveAt(index); working.Widgets.Insert(next, item); Rebind(item.Id); Commit();
    }
    private void PickFont()
    {
        if (Selected is not { } item) return;
        using var initial = new Font(item.FontFamily, item.FontSize, item.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var dialog = new FontDialog { Font = initial, ShowColor = false, MinSize = 5, MaxSize = 90 };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        item.FontFamily = dialog.Font.FontFamily.Name; item.FontSize = Math.Clamp(dialog.Font.SizeInPoints * 96f / 72f, 6, 120); item.Bold = dialog.Font.Bold; Commit();
    }
    private void PickBackground()
    {
        using var dialog = new ColorDialog { Color = FrameRenderer.ParseColor(working.BackgroundColor), FullOpen = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) { working.BackgroundColor = ColorTranslator.ToHtml(dialog.Color).TrimStart('#'); if (!WidgetLayout.IsColor(working.BackgroundColor)) working.BackgroundColor = $"{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"; Commit(); }
    }
    private void Defaults()
    {
        if (MessageBox.Show(this, "Replace this display's widgets with the default layout? The other LCD is unchanged.", "Reset layout", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        working.Widgets = WidgetLayout.Defaults(canvasWidth, canvasHeight); Rebind(working.Widgets[0].Id); Commit();
    }
    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "lcd-layout.png" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        using var renderer = new FrameRenderer(); using var frame = renderer.Render(telemetry(), working, physicalWidth, physicalHeight); frame.Save(dialog.FileName, ImageFormat.Png);
    }
    internal void RenderForTest(string path)
    {
        StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000); ShowInTaskbar = false; Show(); PerformLayout(); canvas.RefreshFrame(); Application.DoEvents();
        using var image = new Bitmap(Width, Height); DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(path, ImageFormat.Png); timer.Stop();
    }
    internal int ExerciseForTest()
    {
        int assertions = 0, initial = working.Widgets.Count;
        void Verify(bool condition, string name) { assertions++; if (!condition) throw new InvalidOperationException("Designer: " + name); }
        Add(); Verify(working.Widgets.Count == initial + 1, "add widget");
        var w = Selected!; var adapter = new WidgetProperties(w);
        adapter.Source = WidgetSource.GpuTemperature; adapter.Text = "GPU {value} °C"; adapter.ValueFormat = "0.0"; adapter.FontFamily = "Consolas"; adapter.FontSize = 24; adapter.Bold = false; adapter.Foreground = Color.Cyan; adapter.Background = Color.Transparent;
        Verify(Commit(), "commit styling"); Verify(Selected!.Source == WidgetSource.GpuTemperature && Selected.FontSize == 24 && Selected.Foreground == "00FFFF" && Selected.Background == "", "source, text, font, colors preserved");
        int x = Selected.X; canvas.NudgeForTest(); Verify(Selected!.X == Math.Min(canvasWidth - Selected.Width, x + 1), "keyboard move");
        int oldWidth = Selected.Width; canvas.ResizeForTest(); Verify(Selected!.Width > oldWidth && Selected.X + Selected.Width <= canvasWidth, "mouse resize stays in bounds");
        Duplicate(); Verify(working.Widgets.Count == initial + 2 && working.Widgets.Select(item => item.Id).Distinct().Count() == initial + 2, "duplicate has an independent ID");
        string id = Selected!.Id; Reorder(-1); Verify(working.Widgets.IndexOf(Selected!) == working.Widgets.Count - 2 && Selected!.Id == id, "change z order");
        new WidgetProperties(Selected!).Width = canvasWidth + 1; Verify(!Commit(), "invalid property rejected"); Verify(Selected!.X + Selected.Width <= canvasWidth, "invalid edit rolls back");
        Delete(); Delete(); Verify(working.Widgets.Count == initial, "delete widgets");
        return assertions;
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

internal sealed class DesignerCanvas : Control
{
    private readonly Func<DisplaySettings> settings;
    private readonly Func<LcdWidget?> selection;
    private readonly Func<Telemetry> telemetry;
    private readonly int width, height;
    private readonly bool round;
    private readonly FrameRenderer renderer = new();
    private Bitmap? frame;
    private Point origin;
    private Rectangle original;
    private bool moving, resizing;
    public event Action<LcdWidget>? SelectionRequested;
    public event Action? GeometryChanged;
    public DesignerCanvas(Func<DisplaySettings> getSettings, Func<LcdWidget?> getSelection, Func<Telemetry> getTelemetry, int w, int h, bool circular)
    { settings = getSettings; selection = getSelection; telemetry = getTelemetry; width = w; height = h; round = circular; Dock = DockStyle.Fill; BackColor = Color.FromArgb(10, 17, 29); DoubleBuffered = true; SetStyle(ControlStyles.Selectable, true); TabStop = true; }
    private RectangleF Area
    {
        get { float scale = Math.Max(.01f, Math.Min((ClientSize.Width - 40f) / width, (ClientSize.Height - 40f) / height)); return new((ClientSize.Width - width * scale) / 2, (ClientSize.Height - height * scale) / 2, width * scale, height * scale); }
    }
    private Point Pixel(Point point) { var a = Area; return new((int)Math.Round((point.X - a.X) * width / a.Width), (int)Math.Round((point.Y - a.Y) * height / a.Height)); }
    private RectangleF View(LcdWidget w) { var a = Area; return new(a.X + w.X * a.Width / width, a.Y + w.Y * a.Height / height, w.Width * a.Width / width, w.Height * a.Height / height); }
    public void RefreshFrame()
    {
        try { var s = settings().Copy(); s.Rotation = 0; var next = renderer.Render(telemetry(), s, width, height); var old = frame; frame = next; old?.Dispose(); }
        catch (Exception) { /* Keep the last valid preview while a media file or property is being edited. */ }
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; var a = Area;
        if (frame is not null) e.Graphics.DrawImage(frame, a);
        using var guide = new Pen(Color.FromArgb(110, 140, 159)) { DashStyle = DashStyle.Dash }; e.Graphics.DrawRectangle(guide, a.X, a.Y, a.Width, a.Height); if (round) e.Graphics.DrawEllipse(guide, a);
        if (selection() is { } item) { var box = View(item); using var pen = new Pen(Color.FromArgb(38, 218, 179), 2); e.Graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height); e.Graphics.FillRectangle(Brushes.White, box.Right - 7, box.Bottom - 7, 10, 10); }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Focus();
        if (selection() is { } current && new RectangleF(View(current).Right - 10, View(current).Bottom - 10, 18, 18).Contains(e.Location)) resizing = true;
        else { var hit = settings().Widgets.AsEnumerable().Reverse().FirstOrDefault(w => View(w).Contains(e.Location)); if (hit is null) return; SelectionRequested?.Invoke(hit); moving = true; }
        if (selection() is not { } selected) return; origin = Pixel(e.Location); original = new(selected.X, selected.Y, selected.Width, selected.Height); Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if ((!moving && !resizing) || selection() is not { } item) return;
        var p = Pixel(e.Location); int dx = p.X - origin.X, dy = p.Y - origin.Y;
        if (resizing) { item.Width = Math.Clamp(original.Width + dx, 8, width - item.X); item.Height = Math.Clamp(original.Height + dy, 8, height - item.Y); }
        else { item.X = Math.Clamp(original.X + dx, 0, width - item.Width); item.Y = Math.Clamp(original.Y + dy, 0, height - item.Height); }
        RefreshFrame();
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (!moving && !resizing) return; moving = resizing = false; Capture = false; GeometryChanged?.Invoke(); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (selection() is not { } w) return; int step = e.Shift ? 10 : 1;
        switch (e.KeyCode) { case Keys.Left: w.X = Math.Max(0, w.X - step); break; case Keys.Right: w.X = Math.Min(width - w.Width, w.X + step); break; case Keys.Up: w.Y = Math.Max(0, w.Y - step); break; case Keys.Down: w.Y = Math.Min(height - w.Height, w.Y + step); break; default: return; }
        e.Handled = true; GeometryChanged?.Invoke();
    }
    internal void NudgeForTest() => OnKeyDown(new KeyEventArgs(Keys.Right));
    internal void ResizeForTest()
    {
        if (selection() is not { } w) throw new InvalidOperationException("Select a widget first.");
        var box = View(w); int x = (int)box.Right, y = (int)box.Bottom;
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0)); OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, x + 12, y + 8, 0)); OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x + 12, y + 8, 0));
    }
    protected override void Dispose(bool disposing) { if (disposing) { frame?.Dispose(); renderer.Dispose(); } base.Dispose(disposing); }
}

internal sealed class WidgetProperties(LcdWidget widget)
{
    [Category("1 · Content")] public string Name { get => widget.Name; set => widget.Name = value; }
    [Category("1 · Content")] public WidgetKind Kind { get => widget.Kind; set => widget.Kind = value; }
    [Category("1 · Content"), Description("Choose a sensor. Source changes set a matching default unit/label; customize Text afterward.")]
    public WidgetSource Source
    {
        get => widget.Source;
        set
        {
            widget.Source = value; widget.ValueFormat = value switch { WidgetSource.Clock => "HH:mm:ss", WidgetSource.Date => "yyyy-MM-dd", WidgetSource.FanRpm or WidgetSource.FanDuty or WidgetSource.CpuLoad or WidgetSource.GpuLoad or WidgetSource.MemoryUsedPercent => "0", _ => "0.0" };
            widget.TextTemplate = value switch
            {
                WidgetSource.CpuTemperature => "CPU {value} °C", WidgetSource.GpuTemperature => "GPU {value} °C",
                WidgetSource.CpuMaximumTemperature => "CPU MAX {value} °C",
                WidgetSource.GpuHotSpotTemperature => "GPU HOTSPOT {value} °C", WidgetSource.GpuMemoryTemperature => "GPU MEMORY {value} °C",
                WidgetSource.CpuLoad => "CPU {value}%", WidgetSource.GpuLoad => "GPU {value}%", WidgetSource.MemoryUsedPercent => "RAM {value}%",
                WidgetSource.FanRpm => "FAN {value} RPM", WidgetSource.FanDuty => "FAN {value}%", WidgetSource.Clock or WidgetSource.Date => "{value}", _ => widget.TextTemplate
            };
        }
    }
    [Category("1 · Content"), Description("Literal text with {value} substituted from the selected source. Text source is entirely literal.")] public string Text { get => widget.TextTemplate; set => widget.TextTemplate = value; }
    [Category("1 · Content"), Description("Numeric: 0 or 0.0. Clock: HH:mm:ss. Date: yyyy-MM-dd. No code is evaluated.")] public string ValueFormat { get => widget.ValueFormat; set => widget.ValueFormat = value; }
    [Category("1 · Content"), Description("Controller port 1–5, used only for FanRpm/FanDuty.")] public int FanPort { get => widget.Port; set => widget.Port = value; }
    [Category("2 · Position / size")] public int X { get => widget.X; set => widget.X = value; }
    [Category("2 · Position / size")] public int Y { get => widget.Y; set => widget.Y = value; }
    [Category("2 · Position / size")] public int Width { get => widget.Width; set => widget.Width = value; }
    [Category("2 · Position / size")] public int Height { get => widget.Height; set => widget.Height = value; }
    [Category("3 · Typography")] public string FontFamily { get => widget.FontFamily; set => widget.FontFamily = value; }
    [Category("3 · Typography"), Description("Font size in physical design pixels, 6–120.")] public float FontSize { get => widget.FontSize; set => widget.FontSize = value; }
    [Category("3 · Typography")] public bool Bold { get => widget.Bold; set => widget.Bold = value; }
    [Category("3 · Typography")] public WidgetAlignment Alignment { get => widget.Alignment; set => widget.Alignment = value; }
    [Category("4 · Colors")] public Color Foreground { get => FrameRenderer.ParseColor(widget.Foreground); set => widget.Foreground = $"{value.R:X2}{value.G:X2}{value.B:X2}"; }
    [Category("4 · Colors"), Description("Transparent leaves the canvas/media visible.")] public Color Background { get => widget.Background.Length == 0 ? Color.Transparent : FrameRenderer.ParseColor(widget.Background); set => widget.Background = value.A == 0 ? "" : $"{value.R:X2}{value.G:X2}{value.B:X2}"; }
    [Category("5 · Radial gauge")] public double Minimum { get => widget.GaugeMinimum; set => widget.GaugeMinimum = value; }
    [Category("5 · Radial gauge")] public double Maximum { get => widget.GaugeMaximum; set => widget.GaugeMaximum = value; }
    [Category("5 · Radial gauge")] public float Thickness { get => widget.GaugeThickness; set => widget.GaugeThickness = value; }
    [Category("5 · Radial gauge")] public float StartAngle { get => widget.GaugeStartAngle; set => widget.GaugeStartAngle = value; }
    [Category("5 · Radial gauge")] public float SweepAngle { get => widget.GaugeSweepAngle; set => widget.GaugeSweepAngle = value; }
    [Category("5 · Radial gauge")] public bool ShowText { get => widget.GaugeShowText; set => widget.GaugeShowText = value; }
    [Category("5 · Radial gauge")] public Color TrackColor { get => FrameRenderer.ParseColor(widget.GaugeTrackColor); set => widget.GaugeTrackColor = $"{value.R:X2}{value.G:X2}{value.B:X2}"; }
}
