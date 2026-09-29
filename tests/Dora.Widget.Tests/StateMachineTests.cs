using Dora.Widget.Abstractions;
using Dora.Widget.Runtime;
using Xunit;
using static Dora.Widget.Abstractions.WidgetInteractionState;

namespace Dora.Widget.Tests;

public class StateMachineTests
{
    [Fact]
    public void Hover_delay_promotes_to_focused()
    {
        var (sm, t) = T.Machine("a");
        sm.PointerEnter("a");
        Assert.Equal(HoverPending, sm.Get("a").InteractionState);
        t.Advance(T.Hover);
        Assert.Equal(Focused, sm.Get("a").InteractionState);
    }

    [Fact]
    public void Leaving_before_delay_returns_to_idle()
    {
        var (sm, t) = T.Machine("a");
        sm.PointerEnter("a");
        t.Advance(TimeSpan.FromMilliseconds(100));
        sm.PointerLeave("a");
        t.Advance(T.Hover);
        Assert.Equal(Idle, sm.Get("a").InteractionState);
    }

    [Fact]
    public void Leave_uses_release_delay_and_return_cancels_it()
    {
        var (sm, t) = T.Machine("a");
        sm.Click("a");
        sm.PointerLeave("a");
        Assert.Equal(FocusReleasePending, sm.Get("a").InteractionState);
        t.Advance(TimeSpan.FromMilliseconds(200));
        sm.PointerEnter("a");
        Assert.Equal(Focused, sm.Get("a").InteractionState);
        t.Advance(T.Release);
        Assert.Equal(Focused, sm.Get("a").InteractionState);

        sm.PointerLeave("a");
        t.Advance(T.Release);
        Assert.Equal(Idle, sm.Get("a").InteractionState);
    }

    [Fact]
    public void Only_one_non_pinned_widget_is_focused()
    {
        var (sm, _) = T.Machine("a", "b", "p");
        sm.Click("p"); sm.Pin("p");
        sm.Click("a");
        sm.Click("b");
        Assert.Equal(Idle, sm.Get("a").InteractionState);
        Assert.Equal(Focused, sm.Get("b").InteractionState);
        Assert.Equal(Pinned, sm.Get("p").InteractionState);
    }

    [Fact]
    public void Pinned_ignores_leave_and_empty_click()
    {
        var (sm, t) = T.Machine("a");
        sm.Click("a"); sm.Pin("a");
        sm.PointerLeave("a");
        t.Advance(TimeSpan.FromSeconds(1));
        sm.ClickEmptyArea();
        Assert.Equal(Pinned, sm.Get("a").InteractionState);
    }

    [Fact]
    public void Pin_requires_focus()
    {
        var (sm, _) = T.Machine("a");
        sm.Pin("a");
        Assert.Equal(Idle, sm.Get("a").InteractionState);
    }

    [Theory]
    [InlineData(true, Focused)]
    [InlineData(false, Idle)]
    public void Unpin_depends_on_pointer(bool over, WidgetInteractionState expected)
    {
        var (sm, _) = T.Machine("a");
        sm.PointerEnter("a"); sm.Click("a"); sm.Pin("a");
        if (!over) sm.PointerLeave("a");
        sm.Unpin("a");
        Assert.Equal(expected, sm.Get("a").InteractionState);
    }

    [Fact]
    public void Empty_click_releases_focus_only()
    {
        var (sm, _) = T.Machine("a", "b");
        sm.Click("a");
        sm.Click("b"); sm.Pin("b");
        sm.ClickEmptyArea();
        Assert.Equal(Idle, sm.Get("a").InteractionState);
        Assert.Equal(Pinned, sm.Get("b").InteractionState);
    }

    [Fact]
    public void Reorder_drag_preserves_pin()
    {
        var (sm, _) = T.Machine("a");
        sm.Click("a"); sm.Pin("a");
        sm.DragStart("a");
        Assert.Equal(Dragging, sm.Get("a").InteractionState);
        Assert.Equal(Pinned, sm.Get("a").PreviousInteractionState);
        sm.DragEnd("a", DragEndKind.Restore);
        Assert.Equal(Pinned, sm.Get("a").InteractionState);
    }

    [Fact]
    public void Drag_cancels_pending_timers()
    {
        var (sm, t) = T.Machine("a");
        sm.PointerEnter("a");
        sm.DragStart("a");
        t.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Dragging, sm.Get("a").InteractionState);
        sm.DragEnd("a", DragEndKind.Restore);
        Assert.Equal(Idle, sm.Get("a").InteractionState);
    }

    [Fact]
    public void Detach_makes_floating_focused_natural()
    {
        var (sm, _) = T.Machine("a");
        sm.DragStart("a");
        sm.DragEnd("a", DragEndKind.DetachToFloating);
        var s = sm.Get("a");
        Assert.Equal(DockState.Floating, s.DockState);
        Assert.Equal(Focused, s.InteractionState);
        Assert.Equal(WidgetDisplayMode.Natural, s.DisplayMode);
    }

    [Fact]
    public void Detach_does_not_steal_focus_from_docked_widgets()
    {
        var (sm, _) = T.Machine("a", "b");
        sm.Click("b");
        sm.DragStart("a");
        sm.DragEnd("a", DragEndKind.DetachToFloating);
        Assert.Equal(Focused, sm.Get("b").InteractionState);
    }

    [Fact]
    public void Redock_becomes_docked_and_focused()
    {
        var (sm, _) = T.Machine("a");
        sm.DragStart("a"); sm.DragEnd("a", DragEndKind.DetachToFloating);
        sm.DragStart("a"); sm.DragEnd("a", DragEndKind.Dock);
        var s = sm.Get("a");
        Assert.Equal(DockState.Docked, s.DockState);
        Assert.Equal(Focused, s.InteractionState);
    }

    [Fact]
    public void Detail_window_is_orthogonal()
    {
        var (sm, _) = T.Machine("a");
        sm.Click("a"); sm.Pin("a");
        sm.SetDetailOpen("a", true);
        Assert.Equal(Pinned, sm.Get("a").InteractionState);
        sm.SetDetailOpen("a", false);
        Assert.Equal(Pinned, sm.Get("a").InteractionState);
        Assert.Equal(DockState.Docked, sm.Get("a").DockState);
    }

    [Fact]
    public void Unregister_cancels_timers()
    {
        var (sm, t) = T.Machine("a");
        sm.PointerEnter("a");
        sm.Unregister("a");
        t.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, t.PendingCount);
    }
}
