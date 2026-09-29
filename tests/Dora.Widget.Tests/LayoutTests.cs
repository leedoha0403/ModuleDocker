using Dora.Widget.Abstractions;
using Dora.Widget.Runtime;
using Xunit;

namespace Dora.Widget.Tests;

public class LayoutTests
{
    private static LayoutItem Item(string id, WidgetInteractionState s = WidgetInteractionState.Idle) =>
        new(id, T.Profile(), s);

    [Fact]
    public void Idle_widgets_are_compact_focused_and_pinned_natural()
    {
        var lm = new AdaptiveLayoutManager(0);
        var r = lm.Compute(new WidgetSize(200, 1000), new[]
        {
            Item("a"), Item("b", WidgetInteractionState.Focused), Item("c", WidgetInteractionState.Pinned)
        });
        Assert.Equal(WidgetDisplayMode.Compact, r.Slots[0].Mode);
        Assert.Equal(WidgetDisplayMode.Natural, r.Slots[1].Mode);
        Assert.Equal(WidgetDisplayMode.Natural, r.Slots[2].Mode);
        Assert.False(r.NeedsScrolling);
    }

    [Fact]
    public void Space_pressure_collapses_lowest_priority_from_the_end()
    {
        var lm = new AdaptiveLayoutManager(0);
        // natural 100 + 3 compact (40) = 220. Available 200 -> one collapse (-20) = 200.
        var r = lm.Compute(new WidgetSize(200, 200), new[]
        {
            Item("f", WidgetInteractionState.Focused), Item("a"), Item("b"), Item("c")
        });
        Assert.Equal(WidgetDisplayMode.Compact, r.Slots[1].Mode);
        Assert.Equal(WidgetDisplayMode.Compact, r.Slots[2].Mode);
        Assert.Equal(WidgetDisplayMode.Collapsed, r.Slots[3].Mode);
        Assert.False(r.NeedsScrolling);
    }

    [Fact]
    public void Hover_pending_is_collapsed_after_idle_ones()
    {
        var lm = new AdaptiveLayoutManager(0);
        var r = lm.Compute(new WidgetSize(200, 60), new[]
        {
            Item("h", WidgetInteractionState.HoverPending), Item("a")
        });
        Assert.Equal(WidgetDisplayMode.Compact, r.Slots[0].Mode);
        Assert.Equal(WidgetDisplayMode.Collapsed, r.Slots[1].Mode);
    }

    [Fact]
    public void Still_insufficient_enables_scrolling_and_never_downgrades_priority_widgets()
    {
        var lm = new AdaptiveLayoutManager(0);
        var r = lm.Compute(new WidgetSize(200, 50), new[]
        {
            Item("p", WidgetInteractionState.Pinned), Item("a")
        });
        Assert.Equal(WidgetDisplayMode.Natural, r.Slots[0].Mode);
        Assert.Equal(WidgetDisplayMode.Collapsed, r.Slots[1].Mode);
        Assert.True(r.NeedsScrolling);
    }

    [Fact]
    public void Narrow_host_downgrades_mode_below_declared_min_width()
    {
        var lm = new AdaptiveLayoutManager(0);
        var r = lm.Compute(new WidgetSize(120, 1000), new[] { Item("p", WidgetInteractionState.Pinned) });
        Assert.Equal(WidgetDisplayMode.Compact, r.Slots[0].Mode); // Natural min width is 150
    }

    [Fact]
    public void Slots_stack_with_spacing()
    {
        var lm = new AdaptiveLayoutManager(4);
        var r = lm.Compute(new WidgetSize(200, 1000), new[] { Item("a"), Item("b") });
        Assert.Equal(0, r.Slots[0].Top);
        Assert.Equal(44, r.Slots[1].Top);
        Assert.Equal(84, r.TotalHeight);
    }

    [Fact]
    public void Empty_list_is_fine()
    {
        var r = new AdaptiveLayoutManager().Compute(new WidgetSize(100, 100), Array.Empty<LayoutItem>());
        Assert.Empty(r.Slots);
        Assert.False(r.NeedsScrolling);
    }
}

public class DockDragTests
{
    [Fact]
    public void Insertion_index_uses_midpoints()
    {
        var slots = new[]
        {
            new LayoutSlot("a", WidgetDisplayMode.Compact, 0, 100, 40),
            new LayoutSlot("b", WidgetDisplayMode.Compact, 44, 100, 40)
        };
        Assert.Equal(0, DockManager.CalculateInsertionIndex(10, slots));
        Assert.Equal(1, DockManager.CalculateInsertionIndex(30, slots));
        Assert.Equal(2, DockManager.CalculateInsertionIndex(200, slots));
    }

    [Theory]
    [InlineData(0, 2, "b", "a", "c")]
    [InlineData(0, 1, "a", "b", "c")]
    [InlineData(0, 0, "a", "b", "c")]
    [InlineData(2, 0, "c", "a", "b")]
    [InlineData(0, 3, "b", "c", "a")]
    public void Move_reorders(int from, int slot, string e0, string e1, string e2)
    {
        var d = new DockManager();
        d.Attach("a"); d.Attach("b"); d.Attach("c");
        d.Move(d.Order[from], slot);
        Assert.Equal(new[] { e0, e1, e2 }, d.Order);
    }

    [Fact]
    public void Attach_is_idempotent_and_detach_removes()
    {
        var d = new DockManager();
        d.Attach("a"); d.Attach("a"); d.Attach("b", 0);
        Assert.Equal(new[] { "b", "a" }, d.Order);
        d.Detach("b");
        Assert.Equal(new[] { "a" }, d.Order);
    }

    [Fact]
    public void Drag_starts_only_after_threshold()
    {
        var dm = new DragManager(new HostOptions { DragThreshold = 6 });
        dm.PointerDown("a", 0, 0, docked: true);
        Assert.False(dm.PointerMove(3, 3));
        Assert.True(dm.PointerMove(5, 5));
        Assert.False(dm.PointerMove(20, 20));
        Assert.True(dm.PointerUp());
    }

    [Fact]
    public void Click_without_drag_is_not_a_drag()
    {
        var dm = new DragManager();
        dm.PointerDown("a", 0, 0, true);
        Assert.False(dm.PointerUp());
    }

    [Theory]
    [InlineData(true, true, DragTarget.Reorder)]
    [InlineData(true, false, DragTarget.Detach)]
    [InlineData(false, true, DragTarget.Dock)]
    [InlineData(false, false, DragTarget.Detach)]
    public void Target_evaluation(bool docked, bool inside, DragTarget expected)
    {
        var dm = new DragManager(new HostOptions { DragThreshold = 1 });
        dm.PointerDown("a", 0, 0, docked);
        dm.PointerMove(10, 10);
        dm.Evaluate(inside, 1);
        Assert.Equal(expected, dm.Target);
    }

    [Fact]
    public void Host_zone_includes_margin()
    {
        var host = new WidgetRect(100, 100, 200, 400);
        Assert.True(DockManager.IsInsideHostZone(host, 90, 300, 24));
        Assert.False(DockManager.IsInsideHostZone(host, 70, 300, 24));
    }
}
