using Dora.Widget.Abstractions;
using Dora.Widget.Runtime;
using Dora.Widget.Runtime.HostWindow;
using Xunit;

namespace Dora.Widget.Tests;

internal static class HW
{
    // 1920x1080 monitor with a 40px taskbar at the bottom (work area 1920x1040).
    public static readonly MonitorInfo Main = new("main", new WidgetRect(0, 0, 1920, 1040), 1.0, true);
    // Second monitor to the right, 150% DPI, 2880x1620 px work area.
    public static readonly MonitorInfo Side = new("side", new WidgetRect(1920, 0, 2880, 1620), 1.5, false);

    public static WidgetRect Win(double x, double y, double w = 300, double h = 500) => new(x, y, w, h);

    public static (HostWindowStateMachine sm, ManualTimerScheduler t) Machine(HostWindowSettings? s = null,
        HostWindowState? initial = null)
    {
        var t = new ManualTimerScheduler();
        return (new HostWindowStateMachine(s ?? new HostWindowSettings(), t, initial), t);
    }

    public static HostWindowSettings SnappedRight(bool autoHide = true) => new()
    {
        AutoHideEnabled = autoHide,
        PlacementState = HostPlacementState.Snapped,
        SnappedEdge = ScreenEdge.Right,
        HandleOffsetRatio = 0.4,
        MonitorId = "main"
    };

    public static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);
}

public class SnapMathTests
{
    [Theory]
    [InlineData(10, ScreenEdge.Left)]      // 10px from the left edge, within 20
    [InlineData(20, ScreenEdge.Left)]      // exactly at the limit
    [InlineData(21, ScreenEdge.None)]      // just outside
    [InlineData(-30, ScreenEdge.Left)]     // dragged partly past the edge still snaps
    public void Left_edge_detection(double x, ScreenEdge expected) =>
        Assert.Equal(expected, SnapMath.DetectEdge(HW.Win(x, 300), HW.Main, 20, SnapEdges.Left | SnapEdges.Right));

    [Fact]
    public void Right_edge_uses_work_area_not_screen()
    {
        // window right side at 1920 - 15 -> 15px from the right work-area edge
        Assert.Equal(ScreenEdge.Right,
            SnapMath.DetectEdge(HW.Win(1920 - 15 - 300, 300), HW.Main, 20, SnapEdges.Left | SnapEdges.Right));
    }

    [Fact]
    public void Bottom_edge_respects_taskbar()
    {
        // Window bottom 10px above the work-area bottom (1040), i.e. 50px above the screen bottom.
        var w = HW.Win(500, 1040 - 10 - 500);
        Assert.Equal(ScreenEdge.Bottom, SnapMath.DetectEdge(w, HW.Main, 20, SnapEdges.Bottom));
        // Flush with the screen bottom would overlap the taskbar: it is beyond the edge -> distance 0, still bottom.
        Assert.Equal(ScreenEdge.Bottom, SnapMath.DetectEdge(HW.Win(500, 1080 - 500), HW.Main, 20, SnapEdges.Bottom));
    }

    [Fact]
    public void Disallowed_edges_are_ignored_and_default_excludes_top_bottom()
    {
        Assert.Equal(ScreenEdge.None, SnapMath.DetectEdge(HW.Win(500, 5), HW.Main, 20, new HostWindowSettings().AllowedEdges));
        Assert.Equal(ScreenEdge.Top, SnapMath.DetectEdge(HW.Win(500, 5), HW.Main, 20, SnapEdges.Top));
    }

    [Fact]
    public void Nearest_edge_wins_in_a_corner()
    {
        var w = HW.Win(5, 12, 300, 300); // 5 from left, 12 from top
        Assert.Equal(ScreenEdge.Left, SnapMath.DetectEdge(w, HW.Main, 20, SnapEdges.Left | SnapEdges.Top));
        var w2 = HW.Win(12, 5, 300, 300);
        Assert.Equal(ScreenEdge.Top, SnapMath.DetectEdge(w2, HW.Main, 20, SnapEdges.Left | SnapEdges.Top));
    }

    [Fact]
    public void Snap_distance_scales_with_monitor_dpi()
    {
        // On the 150% monitor 20 DIP = 30 px.
        var wa = HW.Side.WorkArea;
        Assert.Equal(ScreenEdge.Left, SnapMath.DetectEdge(HW.Win(wa.X + 28, 300), HW.Side, 20, SnapEdges.Left));
        Assert.Equal(ScreenEdge.None, SnapMath.DetectEdge(HW.Win(wa.X + 31, 300), HW.Side, 20, SnapEdges.Left));
        // On the 100% monitor the same 28 px would be too far.
        Assert.Equal(ScreenEdge.None, SnapMath.DetectEdge(HW.Win(28, 300), HW.Main, 20, SnapEdges.Left));
    }

    [Fact]
    public void PlaceAtEdge_is_flush_and_inside_work_area()
    {
        var right = SnapMath.PlaceAtEdge(new WidgetSize(300, 500), HW.Main, ScreenEdge.Right, 0.5);
        Assert.Equal(1620, right.X);
        Assert.Equal(1040 * 0.5 - 250, right.Y);

        var left = SnapMath.PlaceAtEdge(new WidgetSize(300, 500), HW.Main, ScreenEdge.Left, 0);
        Assert.Equal(0, left.X);
        Assert.Equal(0, left.Y); // clamped: ratio 0 would centre above the top

        var bottomEnd = SnapMath.PlaceAtEdge(new WidgetSize(300, 500), HW.Main, ScreenEdge.Left, 1);
        Assert.Equal(1040 - 500, bottomEnd.Y);
    }

    [Fact]
    public void Oversized_window_shrinks_to_work_area()
    {
        var r = SnapMath.PlaceAtEdge(new WidgetSize(300, 5000), HW.Main, ScreenEdge.Right, 0.5);
        Assert.Equal(1040, r.Height);
        Assert.Equal(0, r.Y);
    }

    [Fact]
    public void Top_and_bottom_edges_place_horizontally()
    {
        var top = SnapMath.PlaceAtEdge(new WidgetSize(400, 200), HW.Main, ScreenEdge.Top, 0.25);
        Assert.Equal(0, top.Y);
        Assert.Equal(1920 * 0.25 - 200, top.X);
        var bottom = SnapMath.PlaceAtEdge(new WidgetSize(400, 200), HW.Main, ScreenEdge.Bottom, 0.5);
        Assert.Equal(1040 - 200, bottom.Y);
    }

    [Fact]
    public void AlongEdgeRatio_round_trips_with_PlaceAtEdge()
    {
        var placed = SnapMath.PlaceAtEdge(new WidgetSize(300, 400), HW.Main, ScreenEdge.Right, 0.42);
        Assert.Equal(0.42, SnapMath.AlongEdgeRatio(placed, HW.Main, ScreenEdge.Right), 3);
    }

    [Fact]
    public void Handle_bounds_keep_thickness_on_screen_and_scale_with_dpi()
    {
        var h = SnapMath.HandleBounds(HW.Main, ScreenEdge.Right, 10, 60, 0.4);
        Assert.Equal(1910, h.X);
        Assert.Equal(10, h.Width);
        Assert.Equal(60, h.Height);

        var h2 = SnapMath.HandleBounds(HW.Side, ScreenEdge.Left, 10, 60, 0.4);
        Assert.Equal(HW.Side.WorkArea.X, h2.X);
        Assert.Equal(15, h2.Width);   // 10 DIP * 1.5
        Assert.Equal(90, h2.Height);  // 60 DIP * 1.5

        var top = SnapMath.HandleBounds(HW.Main, ScreenEdge.Top, 12, 80, 0.5);
        Assert.Equal(0, top.Y);
        Assert.Equal(12, top.Height);
        Assert.Equal(80, top.Width);
    }

    [Fact]
    public void Handle_drag_resolution_edge_change_and_detach()
    {
        var allowed = SnapEdges.Left | SnapEdges.Right;
        var same = SnapMath.ResolveHandleDrag((1915, 520), HW.Main, allowed, 120);
        Assert.Equal(ScreenEdge.Right, same.Edge);
        Assert.Equal(520.0 / 1040, same.Ratio, 3);

        var other = SnapMath.ResolveHandleDrag((30, 208), HW.Main, allowed, 120);
        Assert.Equal(ScreenEdge.Left, other.Edge);
        Assert.Equal(0.2, other.Ratio, 3);

        var center = SnapMath.ResolveHandleDrag((960, 500), HW.Main, allowed, 120);
        Assert.Equal(ScreenEdge.None, center.Edge);
    }

    [Fact]
    public void ClampInside_moves_and_shrinks()
    {
        var r = SnapMath.ClampInside(new WidgetRect(-50, 2000, 300, 500), HW.Main.WorkArea);
        Assert.Equal(new WidgetRect(0, 540, 300, 500), r);
    }
}

public class MonitorMathTests
{
    private static readonly IReadOnlyList<MonitorInfo> Both = new[] { HW.Main, HW.Side };

    [Fact]
    public void Pick_uses_largest_overlap()
    {
        Assert.Same(HW.Main, MonitorMath.Pick(Both, HW.Win(100, 100)));
        Assert.Same(HW.Side, MonitorMath.Pick(Both, HW.Win(2000, 100)));
        // straddling: 250px on Main, 50px on Side -> Main
        Assert.Same(HW.Main, MonitorMath.Pick(Both, HW.Win(1670, 100)));
    }

    [Fact]
    public void Pick_falls_back_to_nearest_when_off_screen()
    {
        Assert.Same(HW.Side, MonitorMath.Pick(Both, HW.Win(9000, 100)));
        Assert.Same(HW.Main, MonitorMath.Pick(Both, HW.Win(-5000, 100)));
    }

    [Fact]
    public void Find_falls_back_to_primary_when_monitor_is_gone()
    {
        Assert.Same(HW.Side, MonitorMath.Find(Both, "side"));
        Assert.Same(HW.Main, MonitorMath.Find(Both, "unplugged"));
        Assert.Same(HW.Main, MonitorMath.Find(Both, null));
        Assert.Same(HW.Main, MonitorMath.Find(new[] { HW.Side, HW.Main }, "gone"));
    }

    [Fact]
    public void Restore_snapped_places_on_saved_monitor_with_its_dpi()
    {
        var s = new HostWindowSettings
        {
            PlacementState = HostPlacementState.Snapped,
            SnappedEdge = ScreenEdge.Left,
            HandleOffsetRatio = 0.5,
            MonitorId = "side"
        };
        var r = MonitorMath.Restore(s, Both, new WidgetSize(300, 500));
        Assert.Equal(HostWindowState.SnappedVisible, r.State);
        Assert.Same(HW.Side, r.Monitor);
        Assert.Equal(1920, r.Bounds.X);
        Assert.Equal(450, r.Bounds.Width);   // 300 DIP * 1.5
        Assert.Equal(750, r.Bounds.Height);  // 500 DIP * 1.5
        Assert.Equal(0.5, SnapMath.AlongEdgeRatio(r.Bounds, r.Monitor, ScreenEdge.Left), 3);
    }

    [Fact]
    public void Restore_moves_to_primary_when_monitor_disappeared()
    {
        var s = new HostWindowSettings
        {
            PlacementState = HostPlacementState.Snapped,
            SnappedEdge = ScreenEdge.Right,
            MonitorId = "unplugged"
        };
        var r = MonitorMath.Restore(s, new[] { HW.Main }, new WidgetSize(300, 500));
        Assert.Same(HW.Main, r.Monitor);
        Assert.Equal(1620, r.Bounds.X);
    }

    [Fact]
    public void Restore_floating_rescales_when_dpi_changed_and_clamps()
    {
        // saved at 100% on "side" (offset 100,100 from the work area), monitor is now 150%.
        var s = new HostWindowSettings
        {
            PlacementState = HostPlacementState.Floating,
            MonitorId = "side",
            FloatingBounds = new WidgetRect(1920 + 100, 100, 300, 500),
            FloatingDpiScale = 1.0
        };
        var r = MonitorMath.Restore(s, Both, new WidgetSize(300, 500));
        Assert.Equal(HostWindowState.Floating, r.State);
        Assert.Equal(1920 + 150, r.Bounds.X);
        Assert.Equal(150, r.Bounds.Y);
        Assert.Equal(450, r.Bounds.Width);
        Assert.Equal(750, r.Bounds.Height);
    }

    [Fact]
    public void Restore_floating_on_missing_monitor_is_centred_on_fallback_and_visible()
    {
        var s = new HostWindowSettings
        {
            MonitorId = "gone",
            FloatingBounds = new WidgetRect(5000, 5000, 300, 500),
            FloatingDpiScale = 1.0
        };
        var r = MonitorMath.Restore(s, new[] { HW.Main }, new WidgetSize(300, 500));
        Assert.Same(HW.Main, r.Monitor);
        Assert.Equal((1920 - 300) / 2.0, r.Bounds.X);
        Assert.True(r.Bounds.Y >= 0 && r.Bounds.Y + r.Bounds.Height <= 1040);
    }

    [Fact]
    public void Restore_without_saved_bounds_centres_the_host()
    {
        var r = MonitorMath.Restore(new HostWindowSettings(), Both, new WidgetSize(300, 500));
        Assert.Equal(HostWindowState.Floating, r.State);
        Assert.Same(HW.Main, r.Monitor);
        Assert.Equal((1920 - 300) / 2.0, r.Bounds.X);
    }

    [Fact]
    public void Restore_snapped_with_now_disallowed_edge_becomes_floating()
    {
        var s = new HostWindowSettings
        {
            AllowedEdges = SnapEdges.Left,
            PlacementState = HostPlacementState.Snapped,
            SnappedEdge = ScreenEdge.Right,
            MonitorId = "main"
        };
        Assert.Equal(HostWindowState.Floating, MonitorMath.Restore(s, Both, new WidgetSize(300, 500)).State);
    }
}

public class HostSettingsTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "md-hs-" + Guid.NewGuid().ToString("N"), "s.json");

    [Fact]
    public void Defaults_match_the_spec()
    {
        var s = new HostWindowSettings();
        Assert.True(s.SnapEnabled);
        Assert.Equal(20, s.SnapDistance);
        Assert.Equal(SnapEdges.Left | SnapEdges.Right, s.AllowedEdges);
        Assert.False(s.AutoHideEnabled);
        Assert.Equal(500, s.AutoHideDelayMs);
        Assert.Equal(150, s.RevealDelayMs);
        Assert.InRange(s.HandleThickness, 8, 16);
    }

    [Fact]
    public void Sanitize_clamps_values_and_repairs_inconsistent_placement()
    {
        var s = new HostWindowSettings
        {
            SnapDistance = -5,
            AutoHideDelayMs = -1,
            HandleThickness = 1,
            HandleOffsetRatio = 7,
            PlacementState = HostPlacementState.Snapped,
            SnappedEdge = ScreenEdge.None
        }.Sanitized();
        Assert.Equal(0, s.SnapDistance);
        Assert.Equal(0, s.AutoHideDelayMs);
        Assert.Equal(4, s.HandleThickness);
        Assert.Equal(1, s.HandleOffsetRatio);
        Assert.Equal(HostPlacementState.Floating, s.PlacementState);
    }

    [Fact]
    public void Store_round_trips_using_the_documented_schema()
    {
        var path = TempFile();
        try
        {
            var store = new HostSettingsStore(path);
            var saved = new HostWindowSettings
            {
                AutoHideEnabled = true,
                AllowedEdges = SnapEdges.Left | SnapEdges.Right | SnapEdges.Top,
                PlacementState = HostPlacementState.Snapped,
                SnappedEdge = ScreenEdge.Right,
                HandleOffsetRatio = 0.42,
                MonitorId = "main",
                FloatingBounds = new WidgetRect(100, 200, 300, 400)
            };
            store.Save(saved);
            var text = File.ReadAllText(path);
            Assert.Contains("\"hostWindow\"", text);
            Assert.Contains("\"snappedEdge\": \"Right\"", text);
            Assert.Contains("\"allowedEdges\"", text);
            Assert.Contains("\"Top\"", text);

            var loaded = store.Load();
            Assert.Equal(saved, loaded);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Store_accepts_the_spec_example_and_survives_garbage()
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
            { "hostWindow": { "snapEnabled": true, "snapDistance": 20, "allowedEdges": ["Left","Right"],
              "autoHideEnabled": true, "autoHideDelayMs": 500, "revealDelayMs": 150, "handleThickness": 10,
              "placementState": "Snapped", "snappedEdge": "Right", "handleOffsetRatio": 0.4 } }
            """);
            var store = new HostSettingsStore(path);
            var s = store.Load();
            Assert.True(s.AutoHideEnabled);
            Assert.Equal(ScreenEdge.Right, s.SnappedEdge);
            Assert.Equal(SnapEdges.Left | SnapEdges.Right, s.AllowedEdges);

            File.WriteAllText(path, "{ not json");
            Assert.Equal(new HostWindowSettings(), store.Load());
            File.Delete(path);
            Assert.Equal(new HostWindowSettings(), store.Load());
        }
        finally { Cleanup(path); }
    }

    private static void Cleanup(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

public class HostSettingsRobustnessTests
{
    [Fact]
    public void Unusable_rectangles_and_sizes_are_dropped_so_the_host_never_becomes_a_tiny_window()
    {
        var s = new HostWindowSettings
        {
            FloatingBounds = new WidgetRect(0, 0, 0, 0),
            HostSizeDip = new WidgetSize(0, 0)
        }.Sanitized();
        Assert.Null(s.FloatingBounds);
        Assert.Null(s.HostSizeDip);

        Assert.Null(new HostWindowSettings { FloatingBounds = new WidgetRect(double.NaN, 0, 300, 400) }.Sanitized().FloatingBounds);
        Assert.NotNull(new HostWindowSettings { FloatingBounds = new WidgetRect(-500, 10, 300, 400) }.Sanitized().FloatingBounds);
    }

    [Fact]
    public void Hand_edited_files_with_other_key_casing_still_load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "md-case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "s.json");
            File.WriteAllText(path, "{ \"HostWindow\": { \"AutoHideEnabled\": true, \"FloatingBounds\": { \"X\": 100, \"Y\": 50, \"Width\": 320, \"Height\": 480 } } }");
            var s = new HostSettingsStore(path).Load();
            Assert.True(s.AutoHideEnabled);
            Assert.Equal(new WidgetRect(100, 50, 320, 480), s.FloatingBounds);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class HostInteractionSettingsTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "md-hi-" + Guid.NewGuid().ToString("N"), "s.json");

    [Fact]
    public void Defaults_match_the_spec_values()
    {
        var s = new HostInteractionSettings();
        Assert.Equal(200, s.HoverDelayMs);
        Assert.Equal(300, s.FocusReleaseDelayMs);
        Assert.Equal(6, s.DragThreshold);
    }

    [Fact]
    public void Sanitize_clamps_and_replaces_nan()
    {
        var s = new HostInteractionSettings { HoverDelayMs = -5, FocusReleaseDelayMs = 999999, DragThreshold = double.NaN, ItemSpacing = 500 }.Sanitized();
        Assert.Equal(0, s.HoverDelayMs);
        Assert.Equal(5000, s.FocusReleaseDelayMs);
        Assert.Equal(6, s.DragThreshold);
        Assert.Equal(32, s.ItemSpacing);
    }

    [Fact]
    public void Applying_settings_changes_the_live_options_used_by_the_state_machine()
    {
        var options = new HostOptions();
        var timers = new ManualTimerScheduler();
        var sm = new WidgetStateMachine(timers, options);
        sm.Register(new WidgetRuntimeState { InstanceId = "a", WidgetId = "w" });

        new HostInteractionSettings { HoverDelayMs = 50, FocusReleaseDelayMs = 80 }.ApplyTo(options);
        sm.PointerEnter("a");
        timers.Advance(TimeSpan.FromMilliseconds(49));
        Assert.Equal(WidgetInteractionState.HoverPending, sm.Get("a").InteractionState);
        timers.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(WidgetInteractionState.Focused, sm.Get("a").InteractionState);

        sm.PointerLeave("a");
        timers.Advance(TimeSpan.FromMilliseconds(79));
        Assert.Equal(WidgetInteractionState.FocusReleasePending, sm.Get("a").InteractionState);
        timers.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(WidgetInteractionState.Idle, sm.Get("a").InteractionState);
    }

    [Fact]
    public void Drag_threshold_and_spacing_follow_the_options_live()
    {
        var options = new HostOptions();
        var drag = new DragManager(options);
        drag.PointerDown("a", 0, 0, true);
        new HostInteractionSettings { DragThreshold = 20 }.ApplyTo(options);
        Assert.False(drag.PointerMove(10, 10));   // 14 < 20
        Assert.True(drag.PointerMove(20, 20));

        var sm = new WidgetStateMachine(new ManualTimerScheduler(), options);
        sm.Register(new WidgetRuntimeState { InstanceId = "x", WidgetId = "w" });
        sm.Register(new WidgetRuntimeState { InstanceId = "y", WidgetId = "w" });
        var model = new WidgetHostModel(sm, _ => T.Manifest(), options);
        model.AttachDocked("x"); model.AttachDocked("y");
        new HostInteractionSettings { ItemSpacing = 10 }.ApplyTo(options);
        var layout = model.Layout(new WidgetSize(200, 1000));
        Assert.Equal(40 + 10, layout.Slots[1].Top);   // compact height 40 + new spacing
    }

    [Fact]
    public void Drag_threshold_scales_with_dpi_when_positions_are_pixels()
    {
        var drag = new DragManager(new HostOptions { DragThreshold = 6 });
        drag.PointerDown("a", 0, 0, true);
        Assert.False(drag.PointerMove(10, 0, thresholdScale: 2));   // threshold is 12 px on a 200 % monitor
        Assert.True(drag.PointerMove(13, 0, thresholdScale: 2));
    }

    [Fact]
    public void Store_keeps_both_sections_when_either_is_saved_and_survives_a_missing_section()
    {
        var path = TempFile();
        try
        {
            var store = new HostSettingsStore(path);
            store.SaveInteraction(new HostInteractionSettings { HoverDelayMs = 111 });
            store.Save(new HostWindowSettings { AutoHideEnabled = true });

            var again = new HostSettingsStore(path);
            Assert.Equal(111, again.LoadInteraction().HoverDelayMs);
            Assert.True(again.Load().AutoHideEnabled);

            again.SaveInteraction(new HostInteractionSettings { HoverDelayMs = 222 });
            Assert.True(new HostSettingsStore(path).Load().AutoHideEnabled);

            // a file that only has the old hostWindow section still loads, with default interaction values
            File.WriteAllText(path, "{ \"hostWindow\": { \"autoHideEnabled\": true } }");
            var legacy = new HostSettingsStore(path);
            Assert.True(legacy.Load().AutoHideEnabled);
            Assert.Equal(200, legacy.LoadInteraction().HoverDelayMs);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

public class HostWindowStateMachineTests
{
    private static readonly WidgetSize HostPx = new(300, 500);

    [Fact]
    public void Starts_floating_or_snapped_from_settings()
    {
        Assert.Equal(HostWindowState.Floating, HW.Machine().sm.State);
        Assert.Equal(HostWindowState.SnappedVisible, HW.Machine(HW.SnappedRight(autoHide: false)).sm.State);
    }

    [Fact]
    public void Dragging_near_an_edge_previews_then_snaps_on_drop()
    {
        var (sm, _) = HW.Machine();
        sm.BeginMove();
        Assert.Equal(HostWindowState.Dragging, sm.State);

        Assert.Equal(ScreenEdge.None, sm.Move(HW.Win(800, 100), HW.Main));
        Assert.Equal(HostWindowState.Dragging, sm.State);

        Assert.Equal(ScreenEdge.Right, sm.Move(HW.Win(1610, 100), HW.Main));
        Assert.Equal(HostWindowState.SnapPreview, sm.State);
        Assert.Equal(ScreenEdge.Right, sm.PreviewEdge);

        // moving away cancels the preview
        sm.Move(HW.Win(900, 100), HW.Main);
        Assert.Equal(HostWindowState.Dragging, sm.State);
        sm.Move(HW.Win(1610, 100), HW.Main);

        var result = sm.EndMove(HW.Win(1610, 100), HW.Main);
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
        Assert.Equal(HostPlacementState.Snapped, sm.Placement);
        Assert.Equal(ScreenEdge.Right, sm.SnappedEdge);
        Assert.Equal(1620, result.HostBounds!.Value.X); // flush against the work-area edge
        Assert.Equal("main", sm.Settings.MonitorId);
        Assert.Equal(SnapMath.AlongEdgeRatio(result.HostBounds.Value, HW.Main, ScreenEdge.Right), sm.Settings.HandleOffsetRatio, 3);
    }

    [Fact]
    public void Drop_away_from_edges_floats_and_remembers_bounds_and_dpi()
    {
        var (sm, _) = HW.Machine();
        sm.BeginMove();
        sm.Move(HW.Win(2500, 200), HW.Side);
        var r = sm.EndMove(HW.Win(2500, 200), HW.Side);
        Assert.Equal(HostWindowState.Floating, sm.State);
        Assert.Equal(HostPlacementState.Floating, sm.Placement);
        Assert.Equal(new WidgetRect(2500, 200, 300, 500), sm.Settings.FloatingBounds);
        Assert.Equal(1.5, sm.Settings.FloatingDpiScale);
        Assert.Equal("side", sm.Settings.MonitorId);
        Assert.Equal(r.HostBounds, sm.Settings.FloatingBounds);
    }

    [Fact]
    public void Dragging_a_snapped_host_away_returns_to_floating()
    {
        var (sm, _) = HW.Machine(HW.SnappedRight(autoHide: false));
        sm.BeginMove();
        sm.Move(HW.Win(800, 100), HW.Main);
        sm.EndMove(HW.Win(800, 100), HW.Main);
        Assert.Equal(HostWindowState.Floating, sm.State);
        Assert.Equal(ScreenEdge.None, sm.SnappedEdge);
    }

    [Fact]
    public void Snap_disabled_never_snaps()
    {
        var (sm, _) = HW.Machine(new HostWindowSettings { SnapEnabled = false });
        sm.BeginMove();
        Assert.Equal(ScreenEdge.None, sm.Move(HW.Win(5, 100), HW.Main));
        sm.EndMove(HW.Win(5, 100), HW.Main);
        Assert.Equal(HostWindowState.Floating, sm.State);
    }

    [Fact]
    public void Top_snap_only_when_allowed()
    {
        var (sm, _) = HW.Machine(new HostWindowSettings { AllowedEdges = SnapEdges.Left | SnapEdges.Right | SnapEdges.Top });
        sm.BeginMove();
        var r = sm.EndMove(HW.Win(700, 4, 400, 200), HW.Main);
        Assert.Equal(ScreenEdge.Top, sm.SnappedEdge);
        Assert.Equal(0, r.HostBounds!.Value.Y);
    }

    // ---- auto-hide ----

    [Fact]
    public void Auto_hide_disabled_by_default_never_hides()
    {
        var (sm, t) = HW.Machine(HW.SnappedRight(autoHide: false));
        t.Advance(HW.Ms(10_000));
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
    }

    [Fact]
    public void Hides_after_the_delay_when_nothing_keeps_it_open()
    {
        var (sm, t) = HW.Machine(HW.SnappedRight());
        t.Advance(HW.Ms(499));
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
        t.Advance(HW.Ms(1));
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
    }

    [Theory]
    [InlineData(HostBusy.PointerInsideHost)]
    [InlineData(HostBusy.WidgetDragging)]
    [InlineData(HostBusy.HostDragging)]
    [InlineData(HostBusy.ContextMenuOpen)]
    [InlineData(HostBusy.OwnedPopupOpen)]
    [InlineData(HostBusy.Resizing)]
    [InlineData(HostBusy.DropPreviewShown)]
    public void Each_busy_condition_blocks_hiding(HostBusy busy)
    {
        var (sm, t) = HW.Machine(HW.SnappedRight());
        sm.SetBusy(busy, true);
        t.Advance(HW.Ms(5000));
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);

        // clearing the condition restarts the delay from that moment
        sm.SetBusy(busy, false);
        t.Advance(HW.Ms(499));
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
        t.Advance(HW.Ms(1));
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
    }

    [Fact]
    public void A_condition_that_appears_during_the_delay_cancels_the_hide()
    {
        var (sm, t) = HW.Machine(HW.SnappedRight());
        t.Advance(HW.Ms(400));
        sm.SetBusy(HostBusy.WidgetDragging, true);
        t.Advance(HW.Ms(1000));
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
    }

    [Fact]
    public void Multiple_conditions_must_all_clear()
    {
        var (sm, t) = HW.Machine(HW.SnappedRight());
        sm.SetBusy(HostBusy.PointerInsideHost, true);
        sm.SetBusy(HostBusy.ContextMenuOpen, true);
        sm.SetBusy(HostBusy.PointerInsideHost, false);
        t.Advance(HW.Ms(2000));
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
        sm.SetBusy(HostBusy.ContextMenuOpen, false);
        t.Advance(HW.Ms(500));
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
    }

    [Fact]
    public void Losing_focus_alone_never_hides_and_pointer_inside_still_wins()
    {
        var (sm, t) = HW.Machine(HW.SnappedRight());
        sm.SetBusy(HostBusy.PointerInsideHost, true);
        sm.SetHasFocus(false);
        t.Advance(HW.Ms(5000));
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
    }

    [Fact]
    public void Floating_host_never_auto_hides()
    {
        var (sm, t) = HW.Machine(new HostWindowSettings { AutoHideEnabled = true });
        t.Advance(HW.Ms(5000));
        Assert.Equal(HostWindowState.Floating, sm.State);
    }

    [Fact]
    public void Pinned_widgets_do_not_influence_auto_hide()
    {
        // Widget interaction state is not an input of the Host window machine at all: only HostBusy flags are.
        var (sm, t) = HW.Machine(HW.SnappedRight());
        t.Advance(HW.Ms(500));
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
    }

    // ---- reveal ----

    private static (HostWindowStateMachine sm, ManualTimerScheduler t) Hidden()
    {
        var (sm, t) = HW.Machine(HW.SnappedRight());
        t.Advance(HW.Ms(500));
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
        return (sm, t);
    }

    [Fact]
    public void Handle_hover_reveals_after_the_reveal_delay()
    {
        var (sm, t) = Hidden();
        sm.PointerEnteredHandle();
        Assert.Equal(HostWindowState.RevealPending, sm.State);
        t.Advance(HW.Ms(149));
        Assert.Equal(HostWindowState.RevealPending, sm.State);
        t.Advance(HW.Ms(1));
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
    }

    [Fact]
    public void Leaving_the_handle_early_keeps_it_hidden()
    {
        var (sm, t) = Hidden();
        sm.PointerEnteredHandle();
        t.Advance(HW.Ms(100));
        sm.PointerLeftHandle();
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
        t.Advance(HW.Ms(1000));
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
    }

    [Fact]
    public void Revealed_host_hides_again_if_pointer_never_enters_it()
    {
        var (sm, t) = Hidden();
        sm.PointerEnteredHandle();
        t.Advance(HW.Ms(150));
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
        t.Advance(HW.Ms(500));
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
    }

    [Fact]
    public void Revealed_host_stays_while_pointer_is_inside()
    {
        var (sm, t) = Hidden();
        sm.PointerEnteredHandle();
        t.Advance(HW.Ms(150));
        sm.SetBusy(HostBusy.PointerInsideHost, true);
        t.Advance(HW.Ms(5000));
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
    }

    [Fact]
    public void Reveal_and_HideNow_are_immediate_and_respect_busy()
    {
        var (sm, _) = Hidden();
        sm.Reveal();
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
        sm.SetBusy(HostBusy.ContextMenuOpen, true);
        Assert.False(sm.HideNow());
        sm.SetBusy(HostBusy.ContextMenuOpen, false);
        Assert.True(sm.HideNow());
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
    }

    [Fact]
    public void Widget_state_is_untouched_by_hiding_because_the_machine_has_no_widget_input()
    {
        var (sm, t) = Hidden();
        var states = new List<(HostWindowState, HostWindowState)>();
        sm.StateChanged += (a, b) => states.Add((a, b));
        sm.PointerEnteredHandle();
        t.Advance(HW.Ms(150));
        Assert.Equal(new[]
        {
            (HostWindowState.SnappedHidden, HostWindowState.RevealPending),
            (HostWindowState.RevealPending, HostWindowState.SnappedVisible)
        }, states);
    }

    // ---- handle drag ----

    [Fact]
    public void Dragging_the_handle_along_the_edge_changes_the_offset()
    {
        var (sm, _) = Hidden();
        sm.BeginHandleDrag();
        var p = sm.HandleDragMove((1915, 780), HW.Main);
        Assert.Equal(ScreenEdge.Right, p.Edge);
        Assert.Equal(HostWindowState.SnapPreview, sm.State);

        var r = sm.EndHandleDrag((1915, 780), HW.Main, HostPx);
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
        Assert.Equal(ScreenEdge.Right, sm.SnappedEdge);
        Assert.Equal(0.75, sm.Settings.HandleOffsetRatio, 3);
        Assert.Null(r.HostBounds);
    }

    [Fact]
    public void Dragging_the_handle_to_the_other_edge_switches_edges()
    {
        var (sm, _) = Hidden();
        sm.BeginHandleDrag();
        sm.HandleDragMove((40, 300), HW.Main);
        sm.EndHandleDrag((40, 300), HW.Main, HostPx);
        Assert.Equal(ScreenEdge.Left, sm.SnappedEdge);
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
    }

    [Fact]
    public void Dragging_the_handle_to_the_middle_detaches_the_host()
    {
        var (sm, _) = Hidden();
        sm.BeginHandleDrag();
        var p = sm.HandleDragMove((960, 500), HW.Main);
        Assert.Equal(ScreenEdge.None, p.Edge);
        Assert.Equal(HostWindowState.Dragging, sm.State);

        var r = sm.EndHandleDrag((960, 500), HW.Main, HostPx);
        Assert.Equal(HostWindowState.Floating, sm.State);
        Assert.Equal(HostPlacementState.Floating, sm.Placement);
        Assert.Equal(new WidgetRect(810, 250, 300, 500), r.HostBounds);
    }

    [Fact]
    public void Handle_drag_to_a_disallowed_edge_detaches_instead()
    {
        var (sm, _) = Hidden();
        sm.BeginHandleDrag();
        // The top edge is not allowed by default, so this is "far from every allowed edge".
        var r = sm.EndHandleDrag((960, 3), HW.Main, HostPx);
        Assert.Equal(HostWindowState.Floating, sm.State);
        Assert.NotNull(r.HostBounds);
    }

    [Fact]
    public void Dragging_the_handle_does_not_auto_hide_or_reveal_underneath()
    {
        var (sm, t) = Hidden();
        sm.PointerEnteredHandle();
        sm.PointerLeftHandle();
        sm.BeginHandleDrag();
        t.Advance(HW.Ms(5000));
        Assert.Equal(HostWindowState.Dragging, sm.State);
    }

    // ---- resize ----

    [Fact]
    public void Resizing_blocks_hiding_and_keeps_the_host_flush_afterwards()
    {
        var (sm, t) = HW.Machine(HW.SnappedRight());
        sm.BeginResize();
        Assert.Equal(HostWindowState.Resizing, sm.State);
        t.Advance(HW.Ms(5000));
        Assert.Equal(HostWindowState.Resizing, sm.State);

        var r = sm.EndResize(HW.Win(1500, 100, 420, 600), HW.Main);
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
        Assert.Equal(1500, r.HostBounds!.Value.X); // 1920 - 420
        Assert.Equal(420, r.HostBounds.Value.Width);
        t.Advance(HW.Ms(500));
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
    }

    [Fact]
    public void Resizing_a_floating_host_updates_floating_bounds()
    {
        var (sm, _) = HW.Machine();
        sm.BeginResize();
        sm.EndResize(HW.Win(100, 100, 350, 600), HW.Main);
        Assert.Equal(HostWindowState.Floating, sm.State);
        Assert.Equal(new WidgetRect(100, 100, 350, 600), sm.Settings.FloatingBounds);
    }

    // ---- settings changes ----

    [Fact]
    public void Turning_auto_hide_off_reveals_a_hidden_host()
    {
        var (sm, _) = Hidden();
        sm.ApplySettings(sm.Settings with { AutoHideEnabled = false });
        Assert.Equal(HostWindowState.SnappedVisible, sm.State);
    }

    [Fact]
    public void Turning_auto_hide_on_starts_hiding_a_snapped_visible_host()
    {
        var (sm, t) = HW.Machine(HW.SnappedRight(autoHide: false));
        sm.ApplySettings(sm.Settings with { AutoHideEnabled = true });
        t.Advance(HW.Ms(500));
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
    }

    [Fact]
    public void Turning_snap_off_releases_a_snapped_host_to_floating()
    {
        var (sm, _) = Hidden();
        sm.ApplySettings(sm.Settings with { SnapEnabled = false });
        Assert.Equal(HostWindowState.Floating, sm.State);
        Assert.Equal(ScreenEdge.None, sm.SnappedEdge);
    }

    [Fact]
    public void Disallowing_the_current_edge_releases_the_host()
    {
        var (sm, _) = HW.Machine(HW.SnappedRight(autoHide: false));
        sm.ApplySettings(sm.Settings with { AllowedEdges = SnapEdges.Left });
        Assert.Equal(HostWindowState.Floating, sm.State);
    }

    [Fact]
    public void New_delay_values_apply_and_placement_is_not_overwritten_by_settings_edits()
    {
        var (sm, t) = HW.Machine(HW.SnappedRight());
        var edited = new HostWindowSettings { AutoHideEnabled = true, AutoHideDelayMs = 100 };
        sm.ApplySettings(edited); // its default placement is Floating, but the live placement must win
        Assert.Equal(HostPlacementState.Snapped, sm.Placement);
        Assert.Equal(ScreenEdge.Right, sm.SnappedEdge);
        t.Advance(HW.Ms(100));
        Assert.Equal(HostWindowState.SnappedHidden, sm.State);
    }

    [Fact]
    public void Begin_move_while_hidden_or_moving_is_safe()
    {
        var (sm, _) = Hidden();
        sm.BeginMove();
        sm.BeginMove();
        Assert.Equal(HostWindowState.Dragging, sm.State);
        sm.EndMove(HW.Win(800, 100), HW.Main);
        sm.EndMove(HW.Win(800, 100), HW.Main); // second call is a no-op
        Assert.Equal(HostWindowState.Floating, sm.State);
    }
}
