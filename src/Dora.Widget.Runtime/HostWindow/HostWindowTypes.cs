using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime.HostWindow;

public enum ScreenEdge { None, Left, Right, Top, Bottom }

[Flags]
public enum SnapEdges
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8
}

public enum HostPlacementState { Floating, Snapped }

/// <summary>Transient window state of the main Host (separate from the per-widget state machines).</summary>
public enum HostWindowState
{
    Floating,
    SnapPreview,
    SnappedVisible,
    RevealPending,
    SnappedHidden,
    Dragging,
    Resizing
}

/// <summary>Conditions that must keep the Host visible (auto-hide is suppressed while any is set).</summary>
[Flags]
public enum HostBusy
{
    None = 0,
    PointerInsideHost = 1,
    WidgetDragging = 2,
    HostDragging = 4,
    ContextMenuOpen = 8,
    OwnedPopupOpen = 16,
    Resizing = 32,
    DropPreviewShown = 64
}

/// <summary>
/// One monitor. <see cref="WorkArea"/> is in physical pixels (taskbar excluded) and
/// <see cref="DpiScale"/> converts DIPs to pixels (1.0 = 96 dpi).
/// </summary>
public sealed record MonitorInfo(string Id, WidgetRect WorkArea, double DpiScale, bool IsPrimary);

public static class EdgeExtensions
{
    public static SnapEdges ToFlag(this ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => SnapEdges.Left,
        ScreenEdge.Right => SnapEdges.Right,
        ScreenEdge.Top => SnapEdges.Top,
        ScreenEdge.Bottom => SnapEdges.Bottom,
        _ => SnapEdges.None
    };

    public static bool IsVertical(this ScreenEdge edge) => edge is ScreenEdge.Left or ScreenEdge.Right;
}
