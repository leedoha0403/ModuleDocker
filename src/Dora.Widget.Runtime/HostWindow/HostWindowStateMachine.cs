using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime.HostWindow;

/// <summary>Outcome of a drag that ended: what the window should look like now.</summary>
public sealed record HostPlacementResult(
    HostWindowState State,
    ScreenEdge Edge,
    double HandleOffsetRatio,
    WidgetRect? HostBounds);

/// <summary>
/// State machine for the main Host window (Floating / Snapped / Auto-Hide). It is independent of the
/// per-widget state machines and of the widget contract. Geometry is in physical pixels of the
/// monitor passed in; timers come from <see cref="ITimerScheduler"/> so it is testable.
/// Not thread-safe: call from the UI thread.
/// </summary>
public sealed class HostWindowStateMachine
{
    /// <summary>Distance from every allowed edge (DIP) beyond which dragging the handle detaches the Host.</summary>
    public const double HandleDetachDistanceDip = 120;

    private readonly ITimerScheduler _timers;
    private IDisposable? _hideTimer;
    private IDisposable? _revealTimer;
    private HostBusy _busy;
    private HostWindowState _stateBeforeDrag;

    public HostWindowStateMachine(HostWindowSettings settings, ITimerScheduler timers, HostWindowState? initial = null)
    {
        _timers = timers;
        Settings = settings.Sanitized();
        State = initial ?? (Settings.PlacementState == HostPlacementState.Snapped
            ? HostWindowState.SnappedVisible
            : HostWindowState.Floating);
        Evaluate();
    }

    public HostWindowSettings Settings { get; private set; }
    public HostWindowState State { get; private set; }
    public HostBusy Busy => _busy;
    public bool HasFocus { get; private set; }

    public HostPlacementState Placement => Settings.PlacementState;
    public ScreenEdge SnappedEdge => Settings.SnappedEdge;

    /// <summary>The edge a drop right now would snap to (valid in <see cref="HostWindowState.SnapPreview"/>).</summary>
    public ScreenEdge PreviewEdge { get; private set; }

    public bool IsHidden => State is HostWindowState.SnappedHidden or HostWindowState.RevealPending;

    public event Action<HostWindowState, HostWindowState>? StateChanged;
    public event Action? SettingsChanged;

    // ---- moving the Host window --------------------------------------------------

    public void BeginMove()
    {
        if (State is HostWindowState.Dragging or HostWindowState.SnapPreview) return;
        _stateBeforeDrag = State;
        CancelTimers();
        SetBusy(HostBusy.HostDragging, true, evaluate: false);
        PreviewEdge = ScreenEdge.None;
        Transition(HostWindowState.Dragging);
    }

    /// <summary>Call for every window move step; returns the edge a drop would snap to.</summary>
    public ScreenEdge Move(WidgetRect bounds, MonitorInfo monitor)
    {
        if (State is not (HostWindowState.Dragging or HostWindowState.SnapPreview)) return ScreenEdge.None;
        PreviewEdge = Settings.SnapEnabled
            ? SnapMath.DetectEdge(bounds, monitor, Settings.SnapDistance, Settings.AllowedEdges)
            : ScreenEdge.None;
        Transition(PreviewEdge != ScreenEdge.None ? HostWindowState.SnapPreview : HostWindowState.Dragging);
        return PreviewEdge;
    }

    /// <summary>Ends the move. The caller applies <see cref="HostPlacementResult.HostBounds"/>.</summary>
    public HostPlacementResult EndMove(WidgetRect bounds, MonitorInfo monitor)
    {
        if (State is not (HostWindowState.Dragging or HostWindowState.SnapPreview))
            return Current(null);

        SetBusy(HostBusy.HostDragging, false, evaluate: false);
        var edge = Settings.SnapEnabled
            ? SnapMath.DetectEdge(bounds, monitor, Settings.SnapDistance, Settings.AllowedEdges)
            : ScreenEdge.None;
        PreviewEdge = ScreenEdge.None;

        if (edge != ScreenEdge.None)
        {
            var snapped = SnapMath.SnapTo(bounds, monitor, edge);
            Settings = Settings with
            {
                PlacementState = HostPlacementState.Snapped,
                SnappedEdge = edge,
                HandleOffsetRatio = SnapMath.AlongEdgeRatio(snapped, monitor, edge),
                MonitorId = monitor.Id
            };
            Transition(HostWindowState.SnappedVisible);
            Changed();
            return Current(snapped);
        }

        Settings = Settings with
        {
            PlacementState = HostPlacementState.Floating,
            SnappedEdge = ScreenEdge.None,
            MonitorId = monitor.Id,
            FloatingBounds = bounds,
            FloatingDpiScale = monitor.DpiScale
        };
        Transition(HostWindowState.Floating);
        Changed();
        return Current(bounds);
    }

    // ---- resizing ------------------------------------------------------------------

    public void BeginResize()
    {
        if (State is not (HostWindowState.Floating or HostWindowState.SnappedVisible)) return;
        _stateBeforeDrag = State;
        CancelTimers();
        SetBusy(HostBusy.Resizing, true, evaluate: false);
        Transition(HostWindowState.Resizing);
    }

    /// <summary>Resize finished; a snapped Host stays flush against its edge.</summary>
    public HostPlacementResult EndResize(WidgetRect bounds, MonitorInfo monitor)
    {
        if (State != HostWindowState.Resizing) return Current(null);
        SetBusy(HostBusy.Resizing, false, evaluate: false);

        WidgetRect result = bounds;
        if (Settings.PlacementState == HostPlacementState.Snapped)
        {
            result = SnapMath.SnapTo(bounds, monitor, Settings.SnappedEdge);
            Settings = Settings with
            {
                HandleOffsetRatio = SnapMath.AlongEdgeRatio(result, monitor, Settings.SnappedEdge),
                MonitorId = monitor.Id
            };
            Transition(HostWindowState.SnappedVisible);
        }
        else
        {
            Settings = Settings with { FloatingBounds = bounds, FloatingDpiScale = monitor.DpiScale, MonitorId = monitor.Id };
            Transition(HostWindowState.Floating);
        }
        Changed();
        return Current(result);
    }

    // ---- busy conditions & auto-hide --------------------------------------------------

    /// <summary>Marks a condition that must keep the Host visible (pointer inside, popup open, ...).</summary>
    public void SetBusy(HostBusy flag, bool on) => SetBusy(flag, on, evaluate: true);

    private void SetBusy(HostBusy flag, bool on, bool evaluate)
    {
        var before = _busy;
        _busy = on ? _busy | flag : _busy & ~flag;
        if (evaluate && _busy != before) Evaluate();
    }

    /// <summary>Focus is informational only: losing it never triggers auto-hide by itself.</summary>
    public void SetHasFocus(bool focused) => HasFocus = focused;

    private bool CanAutoHide =>
        Settings.AutoHideEnabled &&
        Settings.PlacementState == HostPlacementState.Snapped &&
        State == HostWindowState.SnappedVisible &&
        _busy == HostBusy.None;

    private void Evaluate()
    {
        if (CanAutoHide)
        {
            if (_hideTimer == null)
                _hideTimer = _timers.Schedule(TimeSpan.FromMilliseconds(Settings.AutoHideDelayMs), OnHideElapsed);
        }
        else
        {
            _hideTimer?.Dispose();
            _hideTimer = null;
        }
    }

    private void OnHideElapsed()
    {
        _hideTimer = null;
        if (!CanAutoHide) return; // conditions changed while waiting
        Transition(HostWindowState.SnappedHidden);
    }

    // ---- handle ---------------------------------------------------------------------------

    public void PointerEnteredHandle()
    {
        if (State != HostWindowState.SnappedHidden) return;
        Transition(HostWindowState.RevealPending);
        _revealTimer = _timers.Schedule(TimeSpan.FromMilliseconds(Settings.RevealDelayMs), () =>
        {
            _revealTimer = null;
            if (State != HostWindowState.RevealPending) return;
            Transition(HostWindowState.SnappedVisible);
        });
    }

    public void PointerLeftHandle()
    {
        if (State != HostWindowState.RevealPending) return;
        _revealTimer?.Dispose();
        _revealTimer = null;
        Transition(HostWindowState.SnappedHidden);
    }

    /// <summary>Shows a hidden Host immediately (e.g. hotkey or programmatic request).</summary>
    public void Reveal()
    {
        if (!IsHidden) return;
        _revealTimer?.Dispose();
        _revealTimer = null;
        Transition(HostWindowState.SnappedVisible);
    }

    /// <summary>
    /// Hides a visible snapped Host right away, regardless of the delay. Busy conditions still apply unless
    /// <paramref name="force"/> is set (the user pressed the fold button while the pointer is over the Host).
    /// </summary>
    public bool HideNow(bool force = false)
    {
        if (Settings.PlacementState != HostPlacementState.Snapped || State != HostWindowState.SnappedVisible ||
            (!force && _busy != HostBusy.None))
            return false;
        _hideTimer?.Dispose();
        _hideTimer = null;
        Transition(HostWindowState.SnappedHidden);
        return true;
    }

    // ---- handle dragging ---------------------------------------------------------------------

    public void BeginHandleDrag()
    {
        if (!IsHidden) return;
        _revealTimer?.Dispose();
        _revealTimer = null;
        _stateBeforeDrag = HostWindowState.SnappedHidden;
        SetBusy(HostBusy.HostDragging, true, evaluate: false);
        PreviewEdge = Settings.SnappedEdge;
        Transition(HostWindowState.Dragging);
    }

    /// <summary>Preview while dragging the handle: the edge/offset it would settle on (None = detach).</summary>
    public (ScreenEdge Edge, double Ratio) HandleDragMove((double X, double Y) pointer, MonitorInfo monitor)
    {
        if (State is not (HostWindowState.Dragging or HostWindowState.SnapPreview)) return (ScreenEdge.None, 0);
        var r = SnapMath.ResolveHandleDrag(pointer, monitor, Settings.AllowedEdges, HandleDetachDistanceDip);
        PreviewEdge = r.Edge;
        Transition(r.Edge != ScreenEdge.None ? HostWindowState.SnapPreview : HostWindowState.Dragging);
        return r;
    }

    /// <summary>
    /// Drops the handle. On an edge the Host stays hidden but the handle moves (same or other edge);
    /// far from every edge the Host detaches and appears as a floating window centred on the pointer.
    /// </summary>
    public HostPlacementResult EndHandleDrag((double X, double Y) pointer, MonitorInfo monitor, WidgetSize hostSizePx)
    {
        if (State is not (HostWindowState.Dragging or HostWindowState.SnapPreview)) return Current(null);
        SetBusy(HostBusy.HostDragging, false, evaluate: false);
        PreviewEdge = ScreenEdge.None;

        var r = SnapMath.ResolveHandleDrag(pointer, monitor, Settings.AllowedEdges, HandleDetachDistanceDip);
        if (r.Edge != ScreenEdge.None)
        {
            Settings = Settings with
            {
                PlacementState = HostPlacementState.Snapped,
                SnappedEdge = r.Edge,
                HandleOffsetRatio = r.Ratio,
                MonitorId = monitor.Id
            };
            Transition(HostWindowState.SnappedHidden);
            Changed();
            return Current(null);
        }

        var bounds = SnapMath.ClampInside(new WidgetRect(
            pointer.X - hostSizePx.Width / 2, pointer.Y - hostSizePx.Height / 2,
            hostSizePx.Width, hostSizePx.Height), monitor.WorkArea);
        Settings = Settings with
        {
            PlacementState = HostPlacementState.Floating,
            SnappedEdge = ScreenEdge.None,
            MonitorId = monitor.Id,
            FloatingBounds = bounds,
            FloatingDpiScale = monitor.DpiScale
        };
        Transition(HostWindowState.Floating);
        Changed();
        return Current(bounds);
    }

    // ---- settings ---------------------------------------------------------------------------------

    /// <summary>Applies changed preferences and repairs any state they invalidate.</summary>
    public void ApplySettings(HostWindowSettings updated)
    {
        var s = updated.Sanitized();
        // Preferences may change while placement is owned by the machine: keep the live placement.
        s = s with
        {
            PlacementState = Settings.PlacementState,
            SnappedEdge = Settings.SnappedEdge,
            HandleOffsetRatio = Settings.HandleOffsetRatio,
            MonitorId = Settings.MonitorId,
            FloatingBounds = Settings.FloatingBounds,
            FloatingDpiScale = Settings.FloatingDpiScale
        };

        var edgeStillValid = s.SnapEnabled && (s.AllowedEdges & s.SnappedEdge.ToFlag()) != 0;
        if (s.PlacementState == HostPlacementState.Snapped && !edgeStillValid)
        {
            // Snapping was turned off (or this edge disallowed): become a normal floating window again.
            s = s with { PlacementState = HostPlacementState.Floating, SnappedEdge = ScreenEdge.None };
            Settings = s;
            CancelTimers();
            if (State is not (HostWindowState.Dragging or HostWindowState.SnapPreview or HostWindowState.Resizing))
                Transition(HostWindowState.Floating);
        }
        else
        {
            Settings = s;
            if (!s.AutoHideEnabled && IsHidden) Reveal();
        }

        // A changed delay must apply to the wait that is already running.
        _hideTimer?.Dispose();
        _hideTimer = null;
        Evaluate();
        SettingsChanged?.Invoke();
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private HostPlacementResult Current(WidgetRect? bounds) =>
        new(State, Settings.SnappedEdge, Settings.HandleOffsetRatio, bounds);

    private void Transition(HostWindowState next)
    {
        if (State == next) return;
        var old = State;
        State = next;
        if (next != HostWindowState.RevealPending)
        {
            _revealTimer?.Dispose();
            _revealTimer = null;
        }
        Evaluate();
        StateChanged?.Invoke(old, next);
    }

    private void CancelTimers()
    {
        _hideTimer?.Dispose();
        _hideTimer = null;
        _revealTimer?.Dispose();
        _revealTimer = null;
    }

    private void Changed() => SettingsChanged?.Invoke();
}
