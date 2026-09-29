using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

public sealed class WidgetRuntimeState
{
    public required string InstanceId { get; init; }
    public required string WidgetId { get; init; }

    public DockState DockState { get; set; } = DockState.Docked;
    public WidgetInteractionState InteractionState { get; set; } = WidgetInteractionState.Idle;
    public WidgetInteractionState PreviousInteractionState { get; set; } = WidgetInteractionState.Idle;
    public WidgetDisplayMode DisplayMode { get; set; } = WidgetDisplayMode.Compact;

    public bool IsPointerOver { get; set; }
    public bool IsDetailOpen { get; set; }

    public int DockOrder { get; set; }
    public WidgetRect? FloatingBounds { get; set; }
}
