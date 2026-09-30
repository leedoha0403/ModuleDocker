namespace Dora.Widget.Runtime;

public enum DragPhase { None, Pending, Dragging }

public enum DragTarget { Reorder, Detach, Dock }

/// <summary>
/// Pointer tracking for one drag gesture: threshold detection and reorder/detach decision.
/// UI code feeds pointer positions; the state machine is driven by the caller.
/// </summary>
public sealed class DragManager
{
    private readonly HostOptions _options;
    private double _startX, _startY;

    public DragManager(HostOptions? options = null) => _options = options ?? new HostOptions();

    public DragPhase Phase { get; private set; }
    public string? InstanceId { get; private set; }
    public bool StartedDocked { get; private set; }

    /// <summary>Last evaluated target while dragging.</summary>
    public DragTarget Target { get; private set; }

    /// <summary>Insertion slot for a reorder/dock target.</summary>
    public int InsertionIndex { get; private set; }

    public void PointerDown(string instanceId, double x, double y, bool docked)
    {
        InstanceId = instanceId;
        StartedDocked = docked;
        _startX = x;
        _startY = y;
        Phase = DragPhase.Pending;
    }

    /// <summary>
    /// Returns true exactly once, when the threshold is crossed. Positions and the threshold are in the
    /// same unit; pass <paramref name="thresholdScale"/> (e.g. the DPI scale) when positions are physical pixels.
    /// </summary>
    public bool PointerMove(double x, double y, double thresholdScale = 1)
    {
        if (Phase != DragPhase.Pending) return false;
        var dx = x - _startX;
        var dy = y - _startY;
        if (Math.Sqrt(dx * dx + dy * dy) < _options.DragThreshold * thresholdScale) return false;
        Phase = DragPhase.Dragging;
        return true;
    }

    /// <summary>Evaluates where a drop would land right now.</summary>
    public void Evaluate(bool insideHostZone, int insertionIndex)
    {
        if (Phase != DragPhase.Dragging) return;
        InsertionIndex = insertionIndex;
        Target = insideHostZone
            ? (StartedDocked ? DragTarget.Reorder : DragTarget.Dock)
            : DragTarget.Detach;
    }

    /// <summary>Ends the gesture; returns whether a real drag (past threshold) happened.</summary>
    public bool PointerUp()
    {
        var wasDragging = Phase == DragPhase.Dragging;
        Phase = DragPhase.None;
        return wasDragging;
    }

    public void Cancel() => Phase = DragPhase.None;
}
