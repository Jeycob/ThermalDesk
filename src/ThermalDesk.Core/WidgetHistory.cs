using System.Text.Json;

namespace ThermalDesk.Core;

public sealed record WidgetHistoryState(DisplaySettings Layout, string? SelectedId);

/// <summary>Bounded, private edit history. Returned layouts never alias stored snapshots.</summary>
public sealed class WidgetHistory
{
    public const int MaximumStates = 50;
    private sealed record Entry(DisplaySettings Layout, string Json, string? SelectedId);
    private readonly List<Entry> entries = [];
    private readonly int physicalWidth, physicalHeight;
    private int position = -1;
    public bool CanUndo => position > 0;
    public bool CanRedo => position >= 0 && position < entries.Count - 1;
    public int Count => entries.Count;

    public WidgetHistory(DisplaySettings initial, int width, int height, string? selectedId)
    {
        physicalWidth = width; physicalHeight = height; Record(initial, selectedId);
    }
    public bool Record(DisplaySettings layout, string? selectedId)
    {
        WidgetLayout.Validate(layout, physicalWidth, physicalHeight);
        string json = JsonSerializer.Serialize(layout, Settings.JsonOptions);
        if (position >= 0 && json == entries[position].Json) return false;
        if (CanRedo) entries.RemoveRange(position + 1, entries.Count - position - 1);
        entries.Add(new(layout.Copy(), json, selectedId));
        if (entries.Count > MaximumStates) entries.RemoveAt(0);
        position = entries.Count - 1; return true;
    }
    public WidgetHistoryState? Undo() => CanUndo ? Restore(--position) : null;
    public WidgetHistoryState? Redo() => CanRedo ? Restore(++position) : null;
    private WidgetHistoryState Restore(int index) => new(entries[index].Layout.Copy(), entries[index].SelectedId);
}
