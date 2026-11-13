namespace ThermalDesk.App;

// Buffer only our layout surfaces. Global WS_EX_COMPOSITED makes scrolling and
// native text controls sluggish, so it is deliberately not used.
internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
{
    public BufferedTableLayoutPanel() => DoubleBuffered = true;
}

internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel() => DoubleBuffered = true;
}

internal sealed class BufferedDataGridView : DataGridView
{
    public BufferedDataGridView() => DoubleBuffered = true;
}

// Native pages and controls remain intact. Only tab headers are drawn to match
// the app; no WebView, animation timer or global compositing layer is added.
internal sealed class ThemedTabControl : TabControl
{
    public ThemedTabControl() { DrawMode = TabDrawMode.OwnerDrawFixed; DoubleBuffered = true; }
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (!IsHandleCreated || TabCount == 0 || message.Msg is not (0x000F or 0x0317 or 0x0318)) return;
        var last = GetTabRect(TabCount - 1);
        if (last.Right >= ClientSize.Width) return;
        using var graphics = message.Msg is 0x0317 or 0x0318 && message.WParam != IntPtr.Zero
            ? Graphics.FromHdc(message.WParam) : Graphics.FromHwnd(Handle);
        using var brush = new SolidBrush(Color.FromArgb(10, 17, 29));
        graphics.FillRectangle(brush, new Rectangle(last.Right + 1, 0, Math.Max(0, ClientSize.Width - last.Right - 1), Math.Max(0, DisplayRectangle.Top - 2)));
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= TabPages.Count) return;
        bool selected = e.Index == SelectedIndex;
        using var background = new SolidBrush(selected ? Color.FromArgb(30, 62, 76) : Color.FromArgb(22, 34, 48));
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, e.Bounds, selected ? Color.White : Color.LightSteelBlue,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (selected)
        { using var accent = new Pen(Color.FromArgb(38, 218, 179), 3); e.Graphics.DrawLine(accent, e.Bounds.Left + 2, e.Bounds.Bottom - 2, e.Bounds.Right - 2, e.Bounds.Bottom - 2); }
        if (Focused && selected) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -4, -4));
    }
}

internal sealed class OwnedPopupMenu : ContextMenuStrip
{
    internal bool ExerciseClosedClickForTest()
    {
        bool clicked = false;
        var action = Items.Add("Test action", null, (_, _) => clicked = true);
        // WinForms dispatches Closed before the selected item's Click.
        OnClosed(new ToolStripDropDownClosedEventArgs(ToolStripDropDownCloseReason.ItemClicked));
        action.PerformClick();
        return clicked && !IsDisposed && !action.IsDisposed;
    }
}
