using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

/// <summary>Maintains dock order and drop-zone logic (spec section 15).</summary>
public sealed class DockManager
{
    private readonly List<string> _order = new();

    public IReadOnlyList<string> Order => _order;

    public event Action? OrderChanged;

    public int IndexOf(string instanceId) => _order.IndexOf(instanceId);

    /// <summary>Appends when index is null or out of range; clamps otherwise.</summary>
    public void Attach(string instanceId, int? index = null)
    {
        if (_order.Contains(instanceId)) return;
        var i = index is null ? _order.Count : Math.Clamp(index.Value, 0, _order.Count);
        _order.Insert(i, instanceId);
        Renumber();
    }

    public void Detach(string instanceId)
    {
        if (_order.Remove(instanceId)) Renumber();
    }

    /// <summary>
    /// Moves an item to an insertion slot expressed in the coordinates of the list
    /// *before* removal (0..Count), as returned by <see cref="CalculateInsertionIndex"/>.
    /// </summary>
    public void Move(string instanceId, int insertionIndex)
    {
        var from = _order.IndexOf(instanceId);
        if (from < 0) return;
        insertionIndex = Math.Clamp(insertionIndex, 0, _order.Count);
        var to = insertionIndex > from ? insertionIndex - 1 : insertionIndex;
        if (to == from) return;
        _order.RemoveAt(from);
        _order.Insert(to, instanceId);
        Renumber();
    }

    /// <summary>
    /// Returns the insertion slot (0..slots.Count) for a pointer Y, comparing against
    /// item midpoints. <paramref name="slots"/> must be in dock order.
    /// </summary>
    public static int CalculateInsertionIndex(double pointerY, IReadOnlyList<LayoutSlot> slots)
    {
        for (var i = 0; i < slots.Count; i++)
            if (pointerY < slots[i].Top + slots[i].Height / 2)
                return i;
        return slots.Count;
    }

    /// <summary>True when the pointer is inside the Host bounds inflated by the detach margin.</summary>
    public static bool IsInsideHostZone(WidgetRect hostBounds, double x, double y, double detachMargin) =>
        x >= hostBounds.X - detachMargin && x <= hostBounds.X + hostBounds.Width + detachMargin &&
        y >= hostBounds.Y - detachMargin && y <= hostBounds.Y + hostBounds.Height + detachMargin;

    public void Sync(IEnumerable<WidgetRuntimeState> states)
    {
        foreach (var s in states) s.DockOrder = Math.Max(0, _order.IndexOf(s.InstanceId));
    }

    private void Renumber() => OrderChanged?.Invoke();
}
