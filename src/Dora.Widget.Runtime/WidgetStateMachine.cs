using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

public enum DragEndKind
{
    /// <summary>Dropped inside the Host (reorder) or cancelled: restore the previous interaction state.</summary>
    Restore,
    /// <summary>Dropped outside the Host: Docked -> Floating, Focused, Natural.</summary>
    DetachToFloating,
    /// <summary>Floating widget dropped on a Host drop zone: Docked + Focused.</summary>
    Dock
}

/// <summary>
/// The single owner of interaction-state changes (spec sections 4-13).
/// Not thread-safe: call from the UI thread only.
/// </summary>
public sealed class WidgetStateMachine
{
    private readonly ITimerScheduler _timers;
    private readonly HostOptions _options;
    private readonly Dictionary<string, WidgetRuntimeState> _states = new();
    private readonly Dictionary<string, IDisposable> _hoverTimers = new();
    private readonly Dictionary<string, IDisposable> _releaseTimers = new();

    public WidgetStateMachine(ITimerScheduler timers, HostOptions? options = null)
    {
        _timers = timers;
        _options = options ?? new HostOptions();
    }

    /// <summary>Raised after any state change of a widget (interaction, dock, detail).</summary>
    public event Action<WidgetRuntimeState>? StateChanged;

    public IReadOnlyCollection<WidgetRuntimeState> States => _states.Values;

    public void Register(WidgetRuntimeState state)
    {
        if (!_states.TryAdd(state.InstanceId, state))
            throw new InvalidOperationException($"Instance '{state.InstanceId}' already registered.");
    }

    public void Unregister(string instanceId)
    {
        CancelTimers(instanceId);
        _states.Remove(instanceId);
    }

    public WidgetRuntimeState Get(string instanceId) =>
        _states.TryGetValue(instanceId, out var s)
            ? s
            : throw new KeyNotFoundException($"Unknown instance '{instanceId}'.");

    // ---- Hover -----------------------------------------------------------

    public void PointerEnter(string id)
    {
        var s = Get(id);
        s.IsPointerOver = true;
        switch (s.InteractionState)
        {
            case WidgetInteractionState.Idle:
                s.InteractionState = WidgetInteractionState.HoverPending;
                CancelTimers(id);
                _hoverTimers[id] = _timers.Schedule(_options.HoverDelay, () => OnHoverElapsed(id));
                Changed(s);
                break;
            case WidgetInteractionState.FocusReleasePending:
                CancelTimers(id);
                s.InteractionState = WidgetInteractionState.Focused;
                Changed(s);
                break;
        }
    }

    public void PointerLeave(string id)
    {
        var s = Get(id);
        s.IsPointerOver = false;
        switch (s.InteractionState)
        {
            case WidgetInteractionState.HoverPending:
                CancelTimers(id);
                s.InteractionState = WidgetInteractionState.Idle;
                Changed(s);
                break;
            case WidgetInteractionState.Focused:
                s.InteractionState = WidgetInteractionState.FocusReleasePending;
                CancelTimers(id);
                _releaseTimers[id] = _timers.Schedule(_options.FocusReleaseDelay, () => OnReleaseElapsed(id));
                Changed(s);
                break;
        }
    }

    private void OnHoverElapsed(string id)
    {
        if (!_states.TryGetValue(id, out var s)) return;
        _hoverTimers.Remove(id);
        if (s.InteractionState != WidgetInteractionState.HoverPending) return;
        BecomeFocused(s);
    }

    private void OnReleaseElapsed(string id)
    {
        if (!_states.TryGetValue(id, out var s)) return;
        _releaseTimers.Remove(id);
        if (s.InteractionState != WidgetInteractionState.FocusReleasePending) return;
        s.InteractionState = WidgetInteractionState.Idle;
        Changed(s);
    }

    // ---- Click / Pin / Release ------------------------------------------

    public void Click(string id)
    {
        var s = Get(id);
        switch (s.InteractionState)
        {
            case WidgetInteractionState.Idle:
            case WidgetInteractionState.HoverPending:
            case WidgetInteractionState.FocusReleasePending:
                BecomeFocused(s);
                break;
            // Focused -> Focused, Pinned unchanged, Dragging ignored.
        }
    }

    public void Pin(string id)
    {
        var s = Get(id);
        if (s.InteractionState is not (WidgetInteractionState.Focused or WidgetInteractionState.FocusReleasePending))
            return;
        CancelTimers(id);
        s.InteractionState = WidgetInteractionState.Pinned;
        Changed(s);
    }

    public void Unpin(string id)
    {
        var s = Get(id);
        if (s.InteractionState != WidgetInteractionState.Pinned) return;
        if (s.IsPointerOver)
            BecomeFocused(s);
        else
        {
            s.InteractionState = WidgetInteractionState.Idle;
            Changed(s);
        }
    }

    /// <summary>Click on empty Host area: releases normal focus, never unpins.</summary>
    public void ClickEmptyArea()
    {
        foreach (var s in _states.Values.ToList())
        {
            if (s.DockState != DockState.Docked) continue;
            if (s.InteractionState is WidgetInteractionState.Focused
                or WidgetInteractionState.FocusReleasePending
                or WidgetInteractionState.HoverPending)
            {
                CancelTimers(s.InstanceId);
                s.InteractionState = WidgetInteractionState.Idle;
                Changed(s);
            }
        }
    }

    // ---- Drag ------------------------------------------------------------

    public void DragStart(string id)
    {
        var s = Get(id);
        if (s.InteractionState == WidgetInteractionState.Dragging) return;
        CancelTimers(id);
        s.PreviousInteractionState = s.InteractionState;
        s.InteractionState = WidgetInteractionState.Dragging;
        Changed(s);
    }

    public void DragEnd(string id, DragEndKind kind)
    {
        var s = Get(id);
        if (s.InteractionState != WidgetInteractionState.Dragging) return;

        switch (kind)
        {
            case DragEndKind.Restore:
                var restored = s.PreviousInteractionState switch
                {
                    WidgetInteractionState.Pinned => WidgetInteractionState.Pinned,
                    WidgetInteractionState.Focused or WidgetInteractionState.FocusReleasePending
                        => WidgetInteractionState.Focused,
                    _ => WidgetInteractionState.Idle
                };
                if (restored == WidgetInteractionState.Focused)
                    BecomeFocused(s);
                else
                {
                    s.InteractionState = restored;
                    Changed(s);
                }
                break;

            case DragEndKind.DetachToFloating:
                s.DockState = DockState.Floating;
                s.DisplayMode = WidgetDisplayMode.Natural;
                if (s.PreviousInteractionState == WidgetInteractionState.Pinned)
                {
                    // Pin survives detaching; only dock state and mode change.
                    s.InteractionState = WidgetInteractionState.Pinned;
                    Changed(s);
                }
                else
                    BecomeFocused(s);
                break;

            case DragEndKind.Dock:
                s.DockState = DockState.Docked;
                if (s.PreviousInteractionState == WidgetInteractionState.Pinned)
                {
                    s.InteractionState = WidgetInteractionState.Pinned;
                    Changed(s);
                }
                else
                    BecomeFocused(s);
                break;
        }
    }

    // ---- Detail window ---------------------------------------------------

    public void SetDetailOpen(string id, bool open)
    {
        var s = Get(id);
        if (s.IsDetailOpen == open) return;
        s.IsDetailOpen = open;
        Changed(s);
    }

    // ---- Internals -------------------------------------------------------

    /// <summary>Makes <paramref name="s"/> the single non-pinned focused widget.</summary>
    private void BecomeFocused(WidgetRuntimeState s)
    {
        CancelTimers(s.InstanceId);
        foreach (var other in _states.Values)
        {
            if (ReferenceEquals(other, s)) continue;
            if (other.DockState != s.DockState) continue;
            if (other.InteractionState is WidgetInteractionState.Focused
                or WidgetInteractionState.FocusReleasePending
                or WidgetInteractionState.HoverPending)
            {
                CancelTimers(other.InstanceId);
                other.InteractionState = WidgetInteractionState.Idle;
                Changed(other);
            }
        }
        s.InteractionState = WidgetInteractionState.Focused;
        Changed(s);
    }

    private void CancelTimers(string id)
    {
        if (_hoverTimers.Remove(id, out var h)) h.Dispose();
        if (_releaseTimers.Remove(id, out var r)) r.Dispose();
    }

    private void Changed(WidgetRuntimeState s) => StateChanged?.Invoke(s);
}
