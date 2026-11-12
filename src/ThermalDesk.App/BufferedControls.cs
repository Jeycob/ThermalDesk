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
