using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

/// <summary>
/// UI-agnostic coordination of dock order, interaction state and adaptive layout.
/// The WPF Host is a thin view over this model.
/// </summary>
public sealed class WidgetHostModel
{
    private readonly WidgetStateMachine _machine;
    private readonly DockManager _dock;
    private readonly AdaptiveLayoutManager _layout;
    private readonly Func<string, WidgetManifest> _manifestOf;

    public WidgetHostModel(
        WidgetStateMachine machine,
        Func<string, WidgetManifest> manifestOf,
        HostOptions? options = null,
        DockManager? dock = null)
    {
        Options = options ?? new HostOptions();
        _machine = machine;
        _manifestOf = manifestOf;
        _dock = dock ?? new DockManager();
        _layout = new AdaptiveLayoutManager(Options.ItemSpacing);
    }

    public HostOptions Options { get; }
    public WidgetStateMachine Machine => _machine;
    public DockManager Dock => _dock;

    public IReadOnlyList<string> DockedOrder => _dock.Order;

    /// <summary>Last computed layout; null until <see cref="Layout"/> runs.</summary>
    public LayoutResult? LastLayout { get; private set; }

    /// <summary>Adds an already-registered instance to the dock.</summary>
    public void AttachDocked(string instanceId, int? index = null)
    {
        var s = _machine.Get(instanceId);
        s.DockState = DockState.Docked;
        _dock.Attach(instanceId, index);
        _dock.Sync(_machine.States);
    }

    public void Remove(string instanceId)
    {
        _dock.Detach(instanceId);
        _dock.Sync(_machine.States);
    }

    public bool CanFloat(string instanceId) => _manifestOf(_machine.Get(instanceId).WidgetId).SupportsFloating;

    /// <summary>Computes the layout and writes the effective display mode back into each state.</summary>
    public LayoutResult Layout(WidgetSize available)
    {
        var items = _dock.Order
            .Where(id => _machine.States.Any(x => x.InstanceId == id))
            .Select(id =>
            {
                var s = _machine.Get(id);
                var interaction = s.InteractionState == WidgetInteractionState.Dragging
                    ? s.PreviousInteractionState
                    : s.InteractionState;
                return new LayoutItem(id, _manifestOf(s.WidgetId).Layout, interaction);
            })
            .ToList();

        var result = _layout.Compute(available, items);
        foreach (var slot in result.Slots)
            _machine.Get(slot.InstanceId).DisplayMode = slot.Mode;
        LastLayout = result;
        return result;
    }

    /// <summary>Insertion slot for a pointer Y (Host-relative) against the last layout.</summary>
    public int InsertionIndexAt(double pointerY) =>
        LastLayout is null ? 0 : DockManager.CalculateInsertionIndex(pointerY, LastLayout.Slots);

    /// <summary>Drop inside the Host: reorder, then restore the previous interaction state.</summary>
    public void DropReorder(string instanceId, int insertionIndex)
    {
        _dock.Move(instanceId, insertionIndex);
        _dock.Sync(_machine.States);
        _machine.DragEnd(instanceId, DragEndKind.Restore);
    }

    /// <summary>Drop outside the Host. Falls back to restore when the widget cannot float.</summary>
    public bool DropDetach(string instanceId, WidgetRect floatingBounds)
    {
        if (!CanFloat(instanceId))
        {
            _machine.DragEnd(instanceId, DragEndKind.Restore);
            return false;
        }
        var s = _machine.Get(instanceId);
        s.FloatingBounds = floatingBounds;
        _dock.Detach(instanceId);
        _machine.DragEnd(instanceId, DragEndKind.DetachToFloating);
        _dock.Sync(_machine.States);
        return true;
    }

    /// <summary>Floating widget dropped on the Host: dock it and let the layout recompute its mode.</summary>
    public void DropDock(string instanceId, int insertionIndex)
    {
        var s = _machine.Get(instanceId);
        _dock.Attach(instanceId, insertionIndex);
        _machine.DragEnd(instanceId, DragEndKind.Dock);
        s.FloatingBounds = null;
        _dock.Sync(_machine.States);
    }

    /// <summary>Menu-style detach without a drag gesture.</summary>
    public bool Float(string instanceId, WidgetRect bounds)
    {
        var s = _machine.Get(instanceId);
        if (s.DockState != DockState.Docked || !CanFloat(instanceId)) return false;
        _machine.DragStart(instanceId);
        return DropDetach(instanceId, bounds);
    }

    /// <summary>Menu-style re-dock at the end of the list.</summary>
    public void Redock(string instanceId)
    {
        var s = _machine.Get(instanceId);
        if (s.DockState != DockState.Floating) return;
        _machine.DragStart(instanceId);
        DropDock(instanceId, _dock.Order.Count);
    }

    /// <summary>Applies a saved layout to instances that already exist in the machine.</summary>
    public void ApplySnapshot(LayoutSnapshot snapshot)
    {
        foreach (var e in snapshot.Entries)
        {
            if (!TryGet(e.InstanceId, out var s)) continue;
            if (e.DockState == DockState.Floating && CanFloat(e.InstanceId))
            {
                s.DockState = DockState.Floating;
                s.DisplayMode = WidgetDisplayMode.Natural;
                s.FloatingBounds = e.FloatingBounds;
            }
        }

        foreach (var e in snapshot.Entries.Where(e => e.DockState == DockState.Docked).OrderBy(e => e.DockOrder))
            if (TryGet(e.InstanceId, out var s) && s.DockState == DockState.Docked)
                AttachDocked(e.InstanceId);

        foreach (var e in snapshot.Entries.Where(e => e.Pinned))
        {
            if (!TryGet(e.InstanceId, out _)) continue;
            _machine.Click(e.InstanceId);
            _machine.Pin(e.InstanceId);
        }
    }

    private bool TryGet(string id, out WidgetRuntimeState s)
    {
        try { s = _machine.Get(id); return true; }
        catch (KeyNotFoundException) { s = null!; return false; }
    }
}
