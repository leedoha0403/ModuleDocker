# Main Docker Host Specification

## 1. Purpose

This document defines the architecture and behavior of the main Docker/Widget Host that manages detachable, reorderable, adaptive mini-widgets.

The Host is responsible for:

- Rendering compatible child widgets/modules
- Docking and undocking
- Reordering widgets by drag
- Adaptive resizing based on available space
- Hover/focus/pin behavior
- Floating windows
- Detail windows on double-click
- Layout persistence
- State coordination
- Common host services

The Host owns interaction behavior. Individual widgets must not implement their own docking engine.

---

## 2. Core UX

Each compatible module can appear in the Host as a mini widget.

### Supported interactions

- Hover: temporarily expand the hovered widget
- Click: focus the widget
- Pin: keep the widget expanded
- Drag inside Host: reorder vertically
- Drag outside Host: detach into a floating window
- Drag floating widget back into Host: dock again
- Double-click: open a separate detail window
- Click empty Host area: release normal focus
- Resize Host: automatically switch widget display modes

---

## 3. State Model

Each widget instance has independent state across four dimensions.

### Dock State

```text
Docked
Floating
```

### Display Mode

```text
Collapsed
Compact
Natural
```

### Interaction State

```text
Idle
HoverPending
Focused
FocusReleasePending
Pinned
Dragging
```

### Detail Window State

```text
Closed
Open
```

Detail window state is orthogonal to Dock/Display/Interaction state.

Example valid state:

```text
Docked + Natural + Pinned + DetailOpen
```

---

## 4. Interaction Priority

When interaction conditions overlap, use this priority:

```text
Dragging
> Pinned
> Focused
> HoverPending
> Idle
```

Display allocation should follow the same priority.

---

## 5. Hover Rules

### Enter

```text
Idle
  -> MouseEnter
HoverPending
  -> hover delay complete
Focused
```

Recommended hover delay:

```text
150-250 ms
```

If the pointer leaves before the delay expires:

```text
HoverPending -> Idle
```

### Leave

Focused widgets should not collapse immediately.

```text
Focused
  -> MouseLeave
FocusReleasePending
  -> release delay complete
Idle
```

Recommended release delay:

```text
200-350 ms
```

If the pointer returns during the release delay:

```text
FocusReleasePending -> Focused
```

---

## 6. Click Rules

Single click makes a widget the active focused widget.

```text
Idle -> Focused
Focused -> Focused
```

Only one non-pinned widget should normally be Focused at once.

When Widget B becomes Focused:

```text
Widget A: Focused -> Idle
Widget B: Idle -> Focused
```

Pinned widgets are not affected.

---

## 7. Pin Rules

Pinning converts a focused widget to Pinned.

```text
Focused -> Pinned
```

Pinned widgets:

- remain expanded
- ignore MouseLeave collapse behavior
- survive normal focus changes
- retain pin state after reorder

Unpin behavior:

```text
Pinned -> Focused   if pointer is over widget
Pinned -> Idle      otherwise
```

---

## 8. Focus Release

Clicking empty Host space releases normal focus.

```text
Focused -> Idle
Pinned -> Pinned
```

Focus release must never implicitly unpin a widget.

---

## 9. Double-click Rules

Double-click opens a separate detail window and does not change Dock or Interaction state.

Examples:

```text
Focused + DetailClosed
-> Focused + DetailOpen
```

```text
Pinned + DetailClosed
-> Pinned + DetailOpen
```

Closing the detail window must not alter the widget's docking or pin state.

---

## 10. Drag Rules

Drag must begin only after the pointer moves beyond a configured threshold.

Recommended behavior:

```text
PointerDown
-> movement < threshold: treat as click/double-click candidate
-> movement >= threshold: start Dragging
```

Before entering Dragging, save the previous interaction state.

```csharp
PreviousInteractionState = InteractionState;
InteractionState = Dragging;
```

---

## 11. Reordering Inside Host

While dragging inside the Host:

1. Determine pointer position
2. Calculate insertion index
3. Render drop indicator
4. On drop, move the widget in the ordered collection
5. Restore previous interaction state

Example:

```text
Pinned -> Dragging -> Pinned
```

Reordering must not clear Pin state.

---

## 12. Detaching Outside Host

Dragging a Docked widget outside the Host detach threshold should show a detach preview.

On drop outside:

```text
Docked -> Floating
Dragging -> Focused
DisplayMode -> Natural
```

The widget's summary content should be hosted inside a floating window.

---

## 13. Re-docking Floating Widgets

A floating widget can be dragged back over a valid Host drop zone.

```text
Floating
-> Dragging
-> DockPreview
-> Drop
-> Docked + Focused
```

After docking, the AdaptiveLayoutManager recalculates DisplayMode.

---

## 14. Adaptive Layout

The Host decides each widget's effective display mode based on:

- available Host size
- Natural/Compact/Collapsed sizes declared by the widget
- current InteractionState
- pinned widgets
- active focused widget
- total widget count

Recommended allocation order:

```text
1. Pinned widgets -> Natural
2. Active Focused widget -> Natural
3. Other widgets -> Compact
4. If space is insufficient -> downgrade non-priority widgets to Collapsed
5. If still insufficient -> enable scrolling
```

The Host must not arbitrarily resize a widget below its declared minimum size for the chosen mode.

---

## 15. Host Components

Recommended structure:

```text
WidgetHost
├─ DockManager
├─ DragManager
├─ FocusManager
├─ AdaptiveLayoutManager
├─ FloatingWindowManager
├─ DetailWindowManager
├─ LayoutManager
├─ WidgetRegistry
├─ WidgetStateMachine
└─ WidgetRuntime
```

### DockManager

- attach/detach widgets
- maintain dock order
- determine drop zones

### DragManager

- drag threshold detection
- pointer tracking
- reorder preview
- detach preview

### FocusManager

- hover timers
- focus ownership
- pin state
- focus release

### AdaptiveLayoutManager

- allocate Natural/Compact/Collapsed modes
- resolve space pressure
- enable scrolling as fallback

### FloatingWindowManager

- create floating windows
- restore floating bounds
- reattach widgets

### DetailWindowManager

- open/close detail windows
- enforce single-instance detail windows per widget instance unless explicitly allowed

### LayoutManager

Persists:

- dock order
- floating bounds
- dock/floating state
- pin state
- Host dimensions/position where applicable

### WidgetStateMachine

All interaction changes should pass through a single state machine.

Do not directly mutate InteractionState from arbitrary UI event handlers.

---

## 16. Runtime State Example

```csharp
public sealed class WidgetRuntimeState
{
    public string InstanceId { get; init; }
    public string WidgetId { get; init; }

    public DockState DockState { get; set; }
    public WidgetInteractionState InteractionState { get; set; }
    public WidgetInteractionState PreviousInteractionState { get; set; }
    public WidgetDisplayMode DisplayMode { get; set; }

    public bool IsPointerOver { get; set; }
    public bool IsDetailOpen { get; set; }

    public int DockOrder { get; set; }
    public Rect? FloatingBounds { get; set; }
}
```

---

## 17. Important Architectural Rule

Do not embed arbitrary external process windows directly using Win32 parenting unless there is no alternative.

Preferred architecture:

```text
Feature.Core
Feature.Presentation
Feature.App
Feature.Widget
```

The standalone application and Host widget should share the same Core/ViewModel/state instead of re-parenting an HWND across processes.

---

## 18. Recommended Default UX Values

```text
HoverDelay:          200 ms
FocusReleaseDelay:   300 ms
DragThreshold:       OS default or 4-8 DIP
Focused limit:       1 non-pinned widget
Pinned count:        multiple allowed
Detail window:       single instance per widget instance
Floating mode:       Natural
```

These values should remain configurable by the Host.
