using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Design;
using System.Globalization;
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
    private readonly WidgetHistory history;
    private readonly Button undo, redo;
    private readonly ContextMenuStrip addMenu = new();
    private readonly ToolTip hints = new() { AutoPopDelay = 18000, InitialDelay = 350, ReshowDelay = 100 };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private bool rebinding;
    public DisplaySettings Result => working.Copy();

    public WidgetDesignerForm(string title, DisplaySettings settings, int width, int height, Func<Telemetry> getTelemetry)
    {
        working = settings.Copy(); physicalWidth = width; physicalHeight = height; telemetry = getTelemetry;
        (canvasWidth, canvasHeight) = WidgetLayout.Canvas(working, width, height);
        if (!working.UseCustomLayout && working.Widgets.Count == 0) working.Widgets = WidgetLayout.Defaults(canvasWidth, canvasHeight);
        working.UseCustomLayout = true; valid = working.Copy();
        history = new(working, width, height, working.Widgets.FirstOrDefault()?.Id);
        Text = title + " · Widget designer"; Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(16, 25, 38); ForeColor = Color.White;
        ClientSize = new Size(1120, 740); MinimumSize = new Size(1000, 680); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        root.RowStyles.Add(new(SizeType.Absolute, 42)); root.RowStyles.Add(new(SizeType.Absolute, 78)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 52)); Controls.Add(root);
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = $"{canvasWidth} × {canvasHeight} design pixels · drag to move · bottom-right handle to resize · arrow keys = 1 px (Shift = 10 px)\nDesign before rotation. The circle guide marks the round LCD crop. Editing never writes to USB.", ForeColor = Color.FromArgb(151, 172, 191) }, 0, 0);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        undo = MakeButton("Undo", () => RestoreHistory(false)); redo = MakeButton("Redo", () => RestoreHistory(true));
        tools.Controls.Add(undo); tools.Controls.Add(redo);
        var add = MakeButton("Add widget ▾", () => { });
        foreach (WidgetKind kind in Enum.GetValues<WidgetKind>())
        {
            var item = new ToolStripMenuItem(WidgetKindConverter.Label(kind));
            item.Click += (_, _) => { try { Add(kind); } catch (ArgumentException e) { MessageBox.Show(this, e.Message, "Widget designer", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }; addMenu.Items.Add(item);
        }
        addMenu.Items.Add(new ToolStripSeparator());
        foreach (var source in new[] { WidgetSource.TextFileLog, WidgetSource.TopCpuProcessLog, WidgetSource.TopGpuProcessLog })
        {
            var item = new ToolStripMenuItem(WidgetSourceConverter.Label(source));
            item.Click += (_, _) => AddLog(source); addMenu.Items.Add(item);
        }
        add.Click += (_, _) => addMenu.Show(add, new Point(0, add.Height));
        hints.SetToolTip(add, "Choose Text, a gauge, Line graph or Bar graph. Select its sensor in Content → Source. Graphs use bounded session history in RAM, without extra sensor polling.");
        tools.Controls.Add(add); tools.Controls.Add(MakeButton("Duplicate", Duplicate)); tools.Controls.Add(MakeButton("Delete", Delete));
        tools.Controls.Add(MakeButton("Back", () => Reorder(-1))); tools.Controls.Add(MakeButton("Front", () => Reorder(1)));
        tools.Controls.Add(MakeButton("Font…", PickFont)); tools.Controls.Add(MakeButton("Canvas color…", PickBackground)); tools.Controls.Add(MakeButton("Defaults", Defaults)); tools.Controls.Add(MakeButton("Export PNG…", Export)); root.Controls.Add(tools, 0, 1);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        columns.ColumnStyles.Add(new(SizeType.Absolute, 170)); columns.ColumnStyles.Add(new(SizeType.Percent, 100)); columns.ColumnStyles.Add(new(SizeType.Absolute, 315)); root.Controls.Add(columns, 0, 2);
        list.BackColor = Color.FromArgb(22, 34, 48); list.ForeColor = Color.White; list.BorderStyle = BorderStyle.None;
        columns.Controls.Add(list, 0, 0);
        canvas = new DesignerCanvas(() => working, () => Selected, telemetry, canvasWidth, canvasHeight, width == height);
        columns.Controls.Add(canvas, 1, 0); columns.Controls.Add(properties, 2, 0);
        properties.BackColor = properties.ViewBackColor = Color.FromArgb(22, 34, 48);
        properties.ForeColor = properties.ViewForeColor = Color.White;
        properties.LineColor = Color.FromArgb(34, 50, 65);
        properties.CategorySplitterColor = Color.FromArgb(16, 25, 38);
        properties.CategoryForeColor = Color.FromArgb(38, 218, 179);
        properties.SelectedItemWithFocusBackColor = Color.FromArgb(32, 89, 111);
        properties.SelectedItemWithFocusForeColor = Color.White;
        properties.DisabledItemForeColor = Color.FromArgb(165, 180, 195);
        properties.ViewBorderColor = properties.HelpBorderColor = Color.FromArgb(52, 72, 91);
        properties.HelpBackColor = BackColor; properties.HelpForeColor = Color.White;
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
        try { WidgetLayout.Validate(working, physicalWidth, physicalHeight); history.Record(working, selectedId); valid = working.Copy(); status.ForeColor = Color.FromArgb(38, 218, 179); status.Text = $"{working.Widgets.Count}/40 widgets · Preview until Apply layout · then saved/applied automatically."; Rebind(selectedId); UpdateHistoryButtons(); return true; }
        catch (ArgumentException e) { working = valid.Copy(); Rebind(selectedId); status.ForeColor = Color.Orange; status.Text = e.Message; return false; }
    }
    private void UpdateHistoryButtons() { undo.Enabled = history.CanUndo; redo.Enabled = history.CanRedo; }
    private void RestoreHistory(bool forward)
    {
        var state = forward ? history.Redo() : history.Undo();
        if (state is null) return;
        working = state.Layout; valid = working.Copy(); Rebind(state.SelectedId); UpdateHistoryButtons();
        status.ForeColor = Color.FromArgb(38, 218, 179);
        status.Text = (forward ? "Redone" : "Undone") + " · Preview only until Apply layout · Ctrl+Z / Ctrl+Y";
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        // Keep the PropertyGrid text editor's native text undo while it has focus.
        if (!properties.ContainsFocus && keys is (Keys.Control | Keys.Z) or (Keys.Control | Keys.Y))
        { RestoreHistory(keys == (Keys.Control | Keys.Y)); return true; }
        return base.ProcessCmdKey(ref message, keys);
    }
    private void Add() => Add(WidgetKind.Text);
    private void AddLog(WidgetSource source)
    {
        string path = "";
        if (source == WidgetSource.TextFileLog)
        {
            using var dialog = new OpenFileDialog { Title = "Select a local log or text file", Filter = "Log / text / PresentMon CSV|*.log;*.txt;*.out;*.err;*.jsonl;*.csv|All files|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            path = dialog.FileName;
        }
        Add(WidgetKind.Text); var widget = Selected!;
        new WidgetProperties(widget).Source = source;
        widget.LogFilePath = path; widget.Width = Math.Min(canvasWidth - 24, 440); widget.Height = Math.Min(canvasHeight - 24, 90);
        widget.LogDisplay = LogDisplayMode.Auto; widget.LogLines = canvasWidth < 180 ? 6 : 3;
        widget.X = 12; widget.Y = 12; widget.Alignment = WidgetAlignment.Left;
        widget.FontFamily = "Consolas"; widget.FontSize = canvasWidth < 240 ? 10 : 14; widget.Bold = false;
        Commit(); status.Text = "Log widget added · read after Apply layout in Live · path/status in Device state";
    }
    private void Add(WidgetKind kind)
    {
        if (working.Widgets.Count >= 40) throw new ArgumentException("Maximum 40 widgets per display.");
        var w = new LcdWidget { Name = "Widget " + (working.Widgets.Count + 1), X = 12, Y = Math.Min(20 + working.Widgets.Count * 10, canvasHeight - 44), Width = Math.Min(220, canvasWidth - 24), Height = 44, FontSize = canvasWidth < 240 ? 18 : 28 };
        w.Kind = kind;
        if (WidgetLayout.IsGraph(kind))
        {
            w.Name = WidgetKindConverter.Label(kind); w.X = w.Y = 8; w.Width = Math.Min(320, canvasWidth - 16);
            w.Height = Math.Min(150, canvasHeight - 16); w.FontSize = 11; w.Foreground = "26DAB3";
        }
        if (WidgetLayout.IsGauge(kind))
        {
            w.Name = WidgetKindConverter.Label(kind) + " " + (working.Widgets.Count + 1);
            w.Foreground = "26DAB3"; w.GaugeTextColor = "FFFFFF"; w.TextTemplate = "{value} °C";
            if (WidgetLayout.IsRadial(kind))
            {
                w.Width = w.Height = Math.Min(180, Math.Min(canvasWidth, canvasHeight) - 24);
                w.FontSize = w.Width < 120 ? 18 : 26;
            }
            else if (kind == WidgetKind.VerticalGauge)
            {
                w.Width = Math.Min(80, canvasWidth - 24); w.Height = Math.Min(200, canvasHeight - 24); w.FontSize = 17;
            }
            w.Y = Math.Min(w.Y, canvasHeight - w.Height - 12);
        }
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
        undo.PerformClick(); Verify(working.Widgets.Count == initial + 1 && redo.Enabled, "Undo restores deleted widget");
        redo.PerformClick(); Verify(working.Widgets.Count == initial && !redo.Enabled, "Redo repeats deletion");
        undo.PerformClick(); Add(); Verify(working.Widgets.Count == initial + 2 && !redo.Enabled, "New edit after Undo discards redo branch");
        string selectedId = Selected!.Id; int beforeMove = Selected.X;
        canvas.NudgeForTest(); undo.PerformClick(); Verify(Selected!.Id == selectedId && Selected.X == beforeMove, "Undo restores keyboard geometry and selection");
        redo.PerformClick(); Verify(Selected!.X == beforeMove + 1, "Redo reapplies keyboard geometry");
        new WidgetProperties(Selected!).Width = canvasWidth + 1; Verify(!Commit(), "Invalid edit remains rejected after undo/redo");
        undo.PerformClick(); Verify(Selected!.X == beforeMove, "Invalid edit did not consume valid history");
        canvas.LoseCaptureForTest();
        Verify(Selected!.X > beforeMove && history.CanUndo && !history.CanRedo, "Interrupted mouse drag commits a new valid state");
        int afterDrag = Selected!.X; canvas.MoveWithoutDraggingForTest();
        Verify(Selected!.X == afterDrag, "Lost mouse capture cannot leave widget stuck to the pointer");
        undo.PerformClick(); Verify(Selected!.X == beforeMove, "Undo restores interrupted drag");
        return assertions;
    }
    protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); addMenu.Dispose(); hints.Dispose(); } base.Dispose(disposing); }
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
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || (!moving && !resizing)) return;
        moving = resizing = false; GeometryChanged?.Invoke();
    }
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
    internal void LoseCaptureForTest()
    {
        if (selection() is not { } w) throw new InvalidOperationException("Select a widget first.");
        var box = View(w); int x = (int)(box.Left + box.Width / 2), y = (int)(box.Top + box.Height / 2);
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
        OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, x + 5, y, 0));
        OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, x + 10, y, 0));
        Capture = false; OnMouseCaptureChanged(EventArgs.Empty);
    }
    internal void MoveWithoutDraggingForTest() => OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, 100, 100, 0));
    protected override void Dispose(bool disposing) { if (disposing) { frame?.Dispose(); renderer.Dispose(); } base.Dispose(disposing); }
}

internal sealed class WidgetProperties(LcdWidget widget)
{
    [Category("1 · Content")] public string Name { get => widget.Name; set => widget.Name = value; }
    [Category("1 · Content"), TypeConverter(typeof(WidgetKindConverter)), Description("Gauges and line/bar history graphs need numeric sensors. Graphs use bounded in-memory session history; configure the window/scale under Graph. Shape changes do not reset your colors or range.")]
    public WidgetKind Kind { get => widget.Kind; set => widget.Kind = value; }
    [Category("1 · Content"), TypeConverter(typeof(WidgetSourceConverter)), Description("Choose a sensor. This resets its text, format and gauge range to useful defaults. VRAM is dedicated GPU memory, not shared RAM. Clocks are reported MHz, not effective transfer rate. Missing sensors display a dash, never a fake zero.")]
    public WidgetSource Source
    {
        get => widget.Source;
        set
        {
            if (value is WidgetSource.TopCpuProcess or WidgetSource.TopGpuProcess || LogWidget.IsLog(value)) widget.Kind = WidgetKind.Text;
            widget.Source = value; widget.ValueFormat = value switch { WidgetSource.Clock => "HH:mm:ss", WidgetSource.Date => "yyyy-MM-dd", WidgetSource.FanRpm or WidgetSource.FanDuty or WidgetSource.CpuLoad or WidgetSource.GpuLoad or WidgetSource.MemoryUsedPercent or WidgetSource.GpuVramUsedPercent or WidgetSource.CpuClockAverageMhz or WidgetSource.CpuClockMaximumMhz or WidgetSource.GpuCoreClockMhz or WidgetSource.GpuMemoryClockMhz => "0", _ => "0.0" };
            widget.GaugeMinimum = 0;
            if (value is WidgetSource.CpuEnergyTodayKwh or WidgetSource.GpuEnergyTodayKwh or WidgetSource.EstimatedTotalEnergyTodayKwh or WidgetSource.CpuEnergy24hProjection or WidgetSource.GpuEnergy24hProjection or WidgetSource.EstimatedTotalEnergy24hProjection or WidgetSource.CpuEnergyLast24hKwh or WidgetSource.GpuEnergyLast24hKwh or WidgetSource.EstimatedTotalEnergyLast24hKwh) widget.ValueFormat = "0.000";
            widget.GaugeMaximum = value switch
            {
                WidgetSource.FanRpm => 2500, WidgetSource.GpuVramUsedGiB or WidgetSource.GpuVramTotalGiB => 16,
                WidgetSource.GpuSharedMemoryUsedGiB or WidgetSource.GpuSharedMemoryTotalGiB => 64,
                WidgetSource.CpuPowerWatts or WidgetSource.CpuPowerAverageHour => 250,
                WidgetSource.GpuPowerWatts or WidgetSource.GpuPowerAverageHour => 450,
                WidgetSource.EstimatedTotalPowerWatts or WidgetSource.EstimatedTotalPowerAverageHour => 800,
                WidgetSource.CpuEnergyTodayKwh or WidgetSource.GpuEnergyTodayKwh or WidgetSource.EstimatedTotalEnergyTodayKwh or WidgetSource.CpuEnergy24hProjection or WidgetSource.GpuEnergy24hProjection or WidgetSource.EstimatedTotalEnergy24hProjection or WidgetSource.CpuEnergyLast24hKwh or WidgetSource.GpuEnergyLast24hKwh or WidgetSource.EstimatedTotalEnergyLast24hKwh => 10,
                WidgetSource.CpuClockAverageMhz or WidgetSource.CpuClockMaximumMhz => 6000,
                WidgetSource.GpuCoreClockMhz => 4000, WidgetSource.GpuMemoryClockMhz => 16000, _ => 100
            };
            widget.TextTemplate = value switch
            {
                WidgetSource.CpuTemperature => "CPU {value} °C", WidgetSource.GpuTemperature => "GPU {value} °C",
                WidgetSource.CpuMaximumTemperature => "CPU MAX {value} °C",
                WidgetSource.GpuHotSpotTemperature => "GPU HOTSPOT {value} °C", WidgetSource.GpuMemoryTemperature => "GPU MEMORY {value} °C",
                WidgetSource.CpuLoad => "CPU {value}%", WidgetSource.GpuLoad => "GPU {value}%", WidgetSource.MemoryUsedPercent => "RAM {value}%",
                WidgetSource.GpuVramUsedGiB => "VRAM {value} GiB", WidgetSource.GpuVramTotalGiB => "VRAM TOTAL {value} GiB", WidgetSource.GpuVramUsedPercent => "VRAM {value}%",
                WidgetSource.GpuSharedMemoryUsedGiB => "SHARED {value} GiB", WidgetSource.GpuSharedMemoryTotalGiB => "SHARED LIMIT {value} GiB",
                WidgetSource.CpuClockAverageMhz => "CPU AVG {value} MHz", WidgetSource.CpuClockMaximumMhz => "CPU MAX {value} MHz",
                WidgetSource.GpuCoreClockMhz => "GPU {value} MHz", WidgetSource.GpuMemoryClockMhz => "GPU MEM {value} MHz",
                WidgetSource.CpuPowerWatts => "CPU {value} W", WidgetSource.GpuPowerWatts => "GPU {value} W", WidgetSource.EstimatedTotalPowerWatts => "PC EST {value} W",
                WidgetSource.CpuPowerAverageHour => "CPU AVG {value} W ({coverage})", WidgetSource.GpuPowerAverageHour => "GPU AVG {value} W ({coverage})", WidgetSource.EstimatedTotalPowerAverageHour => "PC EST AVG {value} W ({coverage})",
                WidgetSource.CpuEnergyTodayKwh => "CPU TODAY {value} kWh /{coverage}", WidgetSource.GpuEnergyTodayKwh => "GPU TODAY {value} kWh /{coverage}", WidgetSource.EstimatedTotalEnergyTodayKwh => "PC EST TODAY {value} kWh /{coverage}",
                WidgetSource.CpuEnergy24hProjection => "CPU 24h EST {value} kWh", WidgetSource.GpuEnergy24hProjection => "GPU 24h EST {value} kWh", WidgetSource.EstimatedTotalEnergy24hProjection => "PC 24h EST {value} kWh",
                WidgetSource.CpuEnergyLast24hKwh or WidgetSource.GpuEnergyLast24hKwh or WidgetSource.EstimatedTotalEnergyLast24hKwh => WidgetLayout.RollingEnergyTemplate(value, widget.Width),
                WidgetSource.TopCpuProcess => "CPU {value}", WidgetSource.TopGpuProcess => "GPU {value}",
                WidgetSource.TextFileLog or WidgetSource.TopCpuProcessLog or WidgetSource.TopGpuProcessLog => "{value}",
                WidgetSource.FanRpm => "FAN {value} RPM", WidgetSource.FanDuty => "FAN {value}%", WidgetSource.Clock or WidgetSource.Date => "{value}", _ => widget.TextTemplate
            };
        }
    }
    [Category("1 · Content"), Description("Literal text with {value} from the selected source. Power averages also support {coverage}: measured duration within the last hour. Keep EST for estimated total power.")] public string Text { get => widget.TextTemplate; set => widget.TextTemplate = value; }
    [Category("1 · Content"), Description("Numeric: 0 or 0.0. Clock: HH:mm:ss. Date: yyyy-MM-dd. No code is evaluated.")] public string ValueFormat { get => widget.ValueFormat; set => widget.ValueFormat = value; }
    [Category("1 · Content"), Description("Controller port 1–5, used only for FanRpm/FanDuty.")] public int FanPort { get => widget.Port; set => widget.Port = value; }
    [Category("6 · Log file"), Editor(typeof(LogPathEditor), typeof(UITypeEditor)), Description("Local text file source: absolute local path; click … to browse. No network/device paths or symlinks. Reads at most the last 32 KiB, only in Live. Automatic process-log sources ignore this field.")]
    public string LogFile { get => widget.LogFilePath; set => widget.LogFilePath = value?.Trim() ?? ""; }
    [Category("6 · Log file"), Description("Last 1–10 lines. Use a small Consolas font and a wide/tall Text widget. Logs may contain private information; common credentials are redacted, not guaranteed anonymous.")]
    public int LastLines { get => widget.LogLines; set => widget.LogLines = value; }
    [Category("6 · Log file"), Description("Auto: prioritize recognized AI/game metrics. AiStats: LAST completed generation/prompt tok/s, context and age, not live streaming speed. Tail also upgrades old automatic GPU widgets to AI stats when rates exist. RawTail: always raw latest lines, preserving the end of long lines. Compact: strip prefixes. Gaming: reported FPS/frame time/network RTT or PresentMon CSV cadence. No game injection or server probing.")]
    public LogDisplayMode LogView { get => widget.LogDisplay; set => widget.LogDisplay = value; }
    [Category("6 · Log file"), Description("Fit text inside this widget, down to 8 px (or your smaller selected size). Structured views prioritize FPS / generation speed; narrow portrait widgets put labels on separate lines.")]
    public bool AutoFitLog { get => widget.LogAutoFit; set => widget.LogAutoFit = value; }
    [Category("6 · Log file"), Description("Mark AI/game observations OLD after 15–600 seconds. AI age follows the inference timestamp, not newer unrelated log lines. LAST / PRES are last observations, not live streaming telemetry. PC LAT is rendering/input latency, never network ping.")]
    public int StaleAfterSeconds { get => widget.LogStaleSeconds; set => widget.LogStaleSeconds = value; }
    [Category("7 · Graph"), Description("Visible history window: 1–60 minutes. Session history stays only in RAM, at most one new point per five seconds. Gaps are not filled with zeros.")]
    public int HistoryMinutes { get => widget.GraphMinutes; set => widget.GraphMinutes = value; }
    [Category("7 · Graph"), Description("Scale to visible measurements. When false, use Gauge Minimum/Maximum as fixed Y bounds. Bar graphs include a zero baseline when automatic.")]
    public bool AutoScale { get => widget.GraphAutoScale; set => widget.GraphAutoScale = value; }
    [Category("7 · Graph"), Description("Show current value/title, Y bounds and time labels when the widget has enough room (at least 80×55 px). Units come from your text template.")]
    public bool ShowGraphLabels { get => widget.GraphShowLabels; set => widget.GraphShowLabels = value; }
    [Category("7 · Graph"), Description("Line/bar thickness in design pixels, 1–6. No effect on the sampler or fan control.")]
    public float LineWidth { get => widget.GraphLineWidth; set => widget.GraphLineWidth = value; }
    [Category("2 · Position / size")] public int X { get => widget.X; set => widget.X = value; }
    [Category("2 · Position / size")] public int Y { get => widget.Y; set => widget.Y = value; }
    [Category("2 · Position / size")] public int Width { get => widget.Width; set => widget.Width = value; }
    [Category("2 · Position / size")] public int Height { get => widget.Height; set => widget.Height = value; }
    [Category("3 · Typography")] public string FontFamily { get => widget.FontFamily; set => widget.FontFamily = value; }
    [Category("3 · Typography"), Description("Font size in physical design pixels, 6–120.")] public float FontSize { get => widget.FontSize; set => widget.FontSize = value; }
    [Category("3 · Typography")] public bool Bold { get => widget.Bold; set => widget.Bold = value; }
    [Category("3 · Typography")] public WidgetAlignment Alignment { get => widget.Alignment; set => widget.Alignment = value; }
    [Category("4 · Colors"), Description("Text color for Text widgets; progress fill / needle color for gauges. Click the dropdown to choose a color.")] public Color Foreground { get => FrameRenderer.ParseColor(widget.Foreground); set => widget.Foreground = $"{value.R:X2}{value.G:X2}{value.B:X2}"; }
    [Category("4 · Colors"), Description("Transparent leaves the canvas/media visible.")] public Color Background { get => widget.Background.Length == 0 ? Color.Transparent : FrameRenderer.ParseColor(widget.Background); set => widget.Background = value.A == 0 ? "" : $"{value.R:X2}{value.G:X2}{value.B:X2}"; }
    [Category("5 · Gauge"), Description("Value at the empty end of the gauge, in the selected source's units. Display only: it never changes fan control.")] public double Minimum { get => widget.GaugeMinimum; set => widget.GaugeMinimum = value; }
    [Category("5 · Gauge"), Description("Value at full scale. Set to your GPU's capacity for a VRAM GiB gauge; use 100 for percent. Above-range values clamp visually, but the text remains the actual value.")] public double Maximum { get => widget.GaugeMaximum; set => widget.GaugeMaximum = value; }
    [Category("5 · Gauge"), Description("Radial stroke width in design pixels. Does not affect horizontal/vertical bars.")] public float Thickness { get => widget.GaugeThickness; set => widget.GaugeThickness = value; }
    [Category("5 · Gauge"), Description("Arc/segments/needle starting angle in degrees: 0 = right, 90 = down. Full ring and semicircle have fixed angles.")] public float StartAngle { get => widget.GaugeStartAngle; set => widget.GaugeStartAngle = value; }
    [Category("5 · Gauge"), Description("Arc/segments/needle sweep in degrees, 1–360 clockwise. Full ring and semicircle ignore this field.")] public float SweepAngle { get => widget.GaugeSweepAngle; set => widget.GaugeSweepAngle = value; }
    [Category("5 · Gauge"), Description("Number of blocks (4–80), used only for the segmented radial style.")] public int Segments { get => widget.GaugeSegments; set => widget.GaugeSegments = value; }
    [Category("5 · Gauge"), Description("Show the formatted sensor value over the gauge. Turn off when using a separate Text widget for precise placement.")] public bool ShowText { get => widget.GaugeShowText; set => widget.GaugeShowText = value; }
    [Category("5 · Gauge"), Description("Color of the empty bar or radial track.")] public Color TrackColor { get => FrameRenderer.ParseColor(widget.GaugeTrackColor); set => widget.GaugeTrackColor = $"{value.R:X2}{value.G:X2}{value.B:X2}"; }
    [Category("5 · Gauge"), Description("Independent gauge text color. Transparent means automatic: white for bars, Foreground for radial gauges.")] public Color TextColor { get => widget.GaugeTextColor.Length == 0 ? Color.Transparent : FrameRenderer.ParseColor(widget.GaugeTextColor); set => widget.GaugeTextColor = value.A == 0 ? "" : $"{value.R:X2}{value.G:X2}{value.B:X2}"; }
}

internal sealed class WidgetKindConverter() : EnumConverter(typeof(WidgetKind))
{
    internal static string Label(WidgetKind value) => value switch
    {
        WidgetKind.Text => "Text / sensor value", WidgetKind.HorizontalGauge => "Horizontal bar", WidgetKind.VerticalGauge => "Vertical bar",
        WidgetKind.RadialGauge => "Radial arc", WidgetKind.RadialSegments => "Radial segments", WidgetKind.RadialNeedle => "Radial needle",
        WidgetKind.RadialRing => "Radial full ring", WidgetKind.RadialSemicircle => "Radial semicircle",
        WidgetKind.LineGraph => "Line graph · history", WidgetKind.BarGraph => "Bar graph · history", _ => value.ToString()
    };
    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) => destinationType == typeof(string) && value is WidgetKind kind ? Label(kind) : base.ConvertTo(context, culture, value, destinationType);
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => value is string text && Enum.GetValues<WidgetKind>().FirstOrDefault(k => Label(k) == text) is var kind && Label(kind) == text ? kind : base.ConvertFrom(context, culture, value);
}

internal sealed class WidgetSourceConverter() : EnumConverter(typeof(WidgetSource))
{
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) => new(Enum.GetValues<WidgetSource>()
        .Where(s => s is not (WidgetSource.CpuEnergy24hProjection or WidgetSource.GpuEnergy24hProjection or WidgetSource.EstimatedTotalEnergy24hProjection)).ToArray());
    internal static string Label(WidgetSource value) => value switch
    {
        WidgetSource.Text => "Static text", WidgetSource.CpuTemperature => "CPU temperature (°C)", WidgetSource.CpuMaximumTemperature => "CPU hottest sensor (°C)",
        WidgetSource.CpuLoad => "CPU load (%)", WidgetSource.CpuClockAverageMhz => "CPU core clocks · average (MHz)", WidgetSource.CpuClockMaximumMhz => "CPU core clocks · maximum (MHz)",
        WidgetSource.GpuTemperature => "GPU core temperature (°C)", WidgetSource.GpuHotSpotTemperature => "GPU hotspot (°C)", WidgetSource.GpuMemoryTemperature => "GPU memory temperature (°C)",
        WidgetSource.GpuLoad => "GPU load (%)", WidgetSource.GpuCoreClockMhz => "GPU core clock (MHz)", WidgetSource.GpuMemoryClockMhz => "GPU memory clock (MHz)",
        WidgetSource.GpuVramUsedGiB => "GPU VRAM used (GiB)", WidgetSource.GpuVramTotalGiB => "GPU VRAM capacity (GiB)", WidgetSource.GpuVramUsedPercent => "GPU VRAM used (%)",
        WidgetSource.GpuSharedMemoryUsedGiB => "GPU shared system RAM used (GiB)", WidgetSource.GpuSharedMemoryTotalGiB => "GPU shared system RAM limit (GiB)",
        WidgetSource.CpuPowerWatts => "CPU package power (W)", WidgetSource.GpuPowerWatts => "GPU reported package/board power (W)", WidgetSource.EstimatedTotalPowerWatts => "Whole PC power · ESTIMATE (W)",
        WidgetSource.CpuPowerAverageHour => "CPU power · last hour average (W)", WidgetSource.GpuPowerAverageHour => "GPU power · last hour average (W)", WidgetSource.EstimatedTotalPowerAverageHour => "Whole PC · ESTIMATED last hour average (W)",
        WidgetSource.CpuEnergyTodayKwh => "CPU energy today · covered intervals (kWh)", WidgetSource.GpuEnergyTodayKwh => "GPU energy today · covered intervals (kWh)", WidgetSource.EstimatedTotalEnergyTodayKwh => "Whole PC energy today · ESTIMATE (kWh)",
        WidgetSource.CpuEnergy24hProjection => "CPU energy · 24h PROJECTION (kWh)", WidgetSource.GpuEnergy24hProjection => "GPU energy · 24h PROJECTION (kWh)", WidgetSource.EstimatedTotalEnergy24hProjection => "Whole PC energy · 24h PROJECTION (kWh)",
        WidgetSource.CpuEnergyLast24hKwh => "CPU energy · observed last 24h (kWh)", WidgetSource.GpuEnergyLast24hKwh => "GPU energy · observed last 24h (kWh)", WidgetSource.EstimatedTotalEnergyLast24hKwh => "Whole PC energy · observed last 24h, approximate (kWh)",
        WidgetSource.MemoryUsedPercent => "System RAM used (%)", WidgetSource.Clock => "Clock", WidgetSource.Date => "Date",
        WidgetSource.TopCpuProcess => "Top CPU process (text · monitoring must be enabled)", WidgetSource.TopGpuProcess => "Top GPU process (text · monitoring must be enabled)",
        WidgetSource.TextFileLog => "Local text file · last lines", WidgetSource.TopCpuProcessLog => "Auto log · top CPU process", WidgetSource.TopGpuProcessLog => "Auto log · top GPU process",
        WidgetSource.FanRpm => "Fan speed (RPM)", WidgetSource.FanDuty => "Fan duty (%)", _ => value.ToString()
    };
    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) => destinationType == typeof(string) && value is WidgetSource source ? Label(source) : base.ConvertTo(context, culture, value, destinationType);
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => value is string text && Enum.GetValues<WidgetSource>().FirstOrDefault(s => Label(s) == text) is var source && Label(source) == text ? source : base.ConvertFrom(context, culture, value);
}

internal sealed class LogPathEditor : UITypeEditor
{
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context) => UITypeEditorEditStyle.Modal;
    public override object? EditValue(ITypeDescriptorContext? context, IServiceProvider provider, object? value)
    {
        using var dialog = new OpenFileDialog { Title = "Select a local log or text file", Filter = "Log / text / PresentMon CSV|*.log;*.txt;*.out;*.err;*.jsonl;*.csv|All files|*.*", CheckFileExists = true };
        if (value is string path && File.Exists(path)) dialog.FileName = path;
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : value;
    }
}
