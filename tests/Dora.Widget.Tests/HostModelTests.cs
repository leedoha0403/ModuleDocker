using Dora.Widget.Abstractions;
using Dora.Widget.Runtime;
using Xunit;

namespace Dora.Widget.Tests;

public class HostModelTests
{
    private static (WidgetHostModel model, WidgetStateMachine sm) Make(int count, bool floating = true)
    {
        var sm = new WidgetStateMachine(new ManualTimerScheduler());
        var manifest = T.Manifest(floating: floating);
        var model = new WidgetHostModel(sm, _ => manifest, new HostOptions { ItemSpacing = 0 });
        for (var i = 0; i < count; i++)
        {
            var id = "i" + i;
            sm.Register(new WidgetRuntimeState { InstanceId = id, WidgetId = "dev.test.sample" });
            model.AttachDocked(id);
        }
        return (model, sm);
    }

    [Fact]
    public void Layout_writes_effective_mode_into_state()
    {
        var (model, sm) = Make(2);
        sm.Click("i0");
        model.Layout(new WidgetSize(200, 1000));
        Assert.Equal(WidgetDisplayMode.Natural, sm.Get("i0").DisplayMode);
        Assert.Equal(WidgetDisplayMode.Compact, sm.Get("i1").DisplayMode);
    }

    [Fact]
    public void Dragging_widget_keeps_its_previous_mode_in_layout()
    {
        var (model, sm) = Make(2);
        sm.Click("i0"); sm.Pin("i0");
        sm.DragStart("i0");
        model.Layout(new WidgetSize(200, 1000));
        Assert.Equal(WidgetDisplayMode.Natural, sm.Get("i0").DisplayMode);
    }

    [Fact]
    public void Reorder_drop_keeps_pin_and_updates_order()
    {
        var (model, sm) = Make(3);
        sm.Click("i0"); sm.Pin("i0");
        sm.DragStart("i0");
        model.Layout(new WidgetSize(200, 1000));
        model.DropReorder("i0", 3);
        Assert.Equal(new[] { "i1", "i2", "i0" }, model.DockedOrder);
        Assert.Equal(WidgetInteractionState.Pinned, sm.Get("i0").InteractionState);
        Assert.Equal(2, sm.Get("i0").DockOrder);
    }

    [Fact]
    public void Detach_then_redock_round_trip()
    {
        var (model, sm) = Make(2);
        sm.DragStart("i0");
        Assert.True(model.DropDetach("i0", new WidgetRect(10, 10, 200, 100)));
        var s = sm.Get("i0");
        Assert.Equal(DockState.Floating, s.DockState);
        Assert.Equal(new[] { "i1" }, model.DockedOrder);
        Assert.Equal(new WidgetRect(10, 10, 200, 100), s.FloatingBounds);

        sm.DragStart("i0");
        model.DropDock("i0", 0);
        Assert.Equal(DockState.Docked, s.DockState);
        Assert.Equal(new[] { "i0", "i1" }, model.DockedOrder);
        Assert.Null(s.FloatingBounds);
        model.Layout(new WidgetSize(200, 1000));
        Assert.Equal(WidgetInteractionState.Focused, s.InteractionState);
    }

    [Fact]
    public void Non_floating_widget_snaps_back_on_detach()
    {
        var (model, sm) = Make(1, floating: false);
        sm.Click("i0"); sm.Pin("i0");
        sm.DragStart("i0");
        Assert.False(model.DropDetach("i0", new WidgetRect(0, 0, 1, 1)));
        Assert.Equal(DockState.Docked, sm.Get("i0").DockState);
        Assert.Equal(WidgetInteractionState.Pinned, sm.Get("i0").InteractionState);
        Assert.False(model.Float("i0", new WidgetRect(0, 0, 1, 1)));
    }

    [Fact]
    public void Menu_float_and_redock()
    {
        var (model, sm) = Make(2);
        Assert.True(model.Float("i1", new WidgetRect(5, 5, 200, 100)));
        Assert.Equal(DockState.Floating, sm.Get("i1").DockState);
        model.Redock("i1");
        Assert.Equal(new[] { "i0", "i1" }, model.DockedOrder);
    }

    [Fact]
    public void Snapshot_round_trip_restores_order_floating_and_pin()
    {
        var (model, sm) = Make(3);
        sm.Click("i2"); sm.Pin("i2");
        model.Float("i0", new WidgetRect(1, 2, 3, 4));
        model.DropReorder(Drag(sm, "i1"), 2); // i1 moves after i2
        var snap = new LayoutManager(new InMemoryLayoutStore()).Capture(sm.States, null);

        var (model2, sm2) = Make(0);
        foreach (var id in new[] { "i0", "i1", "i2" })
            sm2.Register(new WidgetRuntimeState { InstanceId = id, WidgetId = "dev.test.sample" });
        model2.ApplySnapshot(snap);

        Assert.Equal(model.DockedOrder, model2.DockedOrder);
        Assert.Equal(DockState.Floating, sm2.Get("i0").DockState);
        Assert.Equal(new WidgetRect(1, 2, 3, 4), sm2.Get("i0").FloatingBounds);
        Assert.Equal(WidgetInteractionState.Pinned, sm2.Get("i2").InteractionState);
    }

    private static string Drag(WidgetStateMachine sm, string id)
    {
        sm.DragStart(id);
        return id;
    }

    [Fact]
    public void Insertion_index_uses_last_layout()
    {
        var (model, _) = Make(3);
        model.Layout(new WidgetSize(200, 1000));
        Assert.Equal(0, model.InsertionIndexAt(5));
        Assert.Equal(3, model.InsertionIndexAt(900));
    }
}
