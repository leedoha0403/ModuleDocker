using System.Runtime.InteropServices;
using System.Windows;
using Dora.Widget.Abstractions;
using Dora.Widget.Host;
using Dora.Widget.Host.HostWindow;
using Dora.Widget.Runtime;
using Dora.Widget.Runtime.HostWindow;

namespace Dora.Widget.Host.Tests;

internal sealed class FakeMonitors : IMonitorProvider
{
    public List<MonitorInfo> List { get; set; } = new()
    {
        new MonitorInfo("main", new WidgetRect(0, 0, 1920, 1040), 1.0, true)
    };
    public IReadOnlyList<MonitorInfo> GetMonitors() => List;
}

internal sealed class PlacementRig : IDisposable
{
    public readonly Window Host;
    public readonly FakeMonitors Monitors = new();
    public readonly ManualTimerScheduler Timers = new();
    public readonly List<HostWindowSettings> Saved = new();
    public readonly HostWindowController Controller;
    public Point Cursor = new(-1000, -1000);

    public PlacementRig(HostWindowSettings? settings = null, Action<Window>? configure = null)
    {
        // borderless like the real Host window: a captioned window cannot be narrowed below the OS minimum track width
        Host = new Window { Width = 300, Height = 500, Content = new System.Windows.Controls.Border(), ShowActivated = false, WindowStyle = WindowStyle.None };
        configure?.Invoke(Host);
        Controller = new HostWindowController(Host, settings ?? new HostWindowSettings(), Monitors, Timers, Saved.Add)
        {
            AnimationsEnabled = false
        };
        Controller.CursorPx = () => Cursor;
        Controller.Initialize();
        Host.Show();
        Sta.Pump();
    }

    public WidgetRect HostBounds => Native.GetBoundsPx(Native.Handle(Host));
    public WidgetRect HandleBounds => Native.GetBoundsPx(Native.Handle(Controller.Handle));
    public HostWindowState State => Controller.Machine.State;

    public void Advance(int ms)
    {
        Timers.Advance(TimeSpan.FromMilliseconds(ms));
        Sta.Pump();
    }

    public void Dispose()
    {
        Controller.Dispose();
        Host.Close();
    }

    // ---- window message helpers ----

    public void BeginMove()
    {
        Controller.DispatchMessage(Native.WmSysCommand, new IntPtr(Native.ScMove | 2), IntPtr.Zero);
        Controller.DispatchMessage(Native.WmEnterSizeMove, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Sends WM_MOVING and returns the rectangle Windows would use afterwards.</summary>
    public (bool Handled, WidgetRect Rect) Moving(WidgetRect r)
    {
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.RECT>());
        try
        {
            Marshal.StructureToPtr(new Native.RECT
            {
                Left = (int)r.X, Top = (int)r.Y, Right = (int)(r.X + r.Width), Bottom = (int)(r.Y + r.Height)
            }, ptr, false);
            var (handled, _) = Controller.DispatchMessage(Native.WmMoving, IntPtr.Zero, ptr);
            var o = Marshal.PtrToStructure<Native.RECT>(ptr);
            return (handled, new WidgetRect(o.Left, o.Top, o.Right - o.Left, o.Bottom - o.Top));
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void EndMove(WidgetRect finalBounds)
    {
        Native.SetBoundsPx(Native.Handle(Host), finalBounds);
        Controller.DispatchMessage(Native.WmExitSizeMove, IntPtr.Zero, IntPtr.Zero);
        Sta.Pump();
    }

    public void BeginResize()
    {
        Controller.DispatchMessage(Native.WmSysCommand, new IntPtr(Native.ScSize | 2), IntPtr.Zero);
        Controller.DispatchMessage(Native.WmEnterSizeMove, IntPtr.Zero, IntPtr.Zero);
    }
}

public class HostWindowIntegrationTests
{
    private static HostWindowSettings SnappedRight(bool autoHide = false) => new()
    {
        AutoHideEnabled = autoHide,
        PlacementState = HostPlacementState.Snapped,
        SnappedEdge = ScreenEdge.Right,
        HandleOffsetRatio = 0.5,
        MonitorId = "main",
        HandleThickness = 10,
        HostSizeDip = new WidgetSize(300, 500)
    };

    [Fact]
    public void Snapped_settings_restore_flush_to_the_edge_and_force_topmost()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight());
            var b = rig.HostBounds;
            Assert.Equal(1920, b.X + b.Width);          // right edge of the work area
            Assert.Equal(1040 * 0.5 - b.Height / 2, b.Y, 0);
            Assert.True(rig.Host.Topmost);
            Assert.Equal(HostWindowState.SnappedVisible, rig.State);
        });
    }

    [Fact]
    public void Floating_settings_restore_saved_bounds()
    {
        Sta.Run(() =>
        {
            var s = new HostWindowSettings
            {
                MonitorId = "main",
                FloatingBounds = new WidgetRect(400, 200, 320, 480),
                FloatingDpiScale = 1.0
            };
            using var rig = new PlacementRig(s);
            Assert.Equal(new WidgetRect(400, 200, 320, 480), rig.HostBounds);
            Assert.False(rig.Host.Topmost);
        });
    }

    [Fact]
    public void Auto_hide_hides_host_and_shows_only_the_handle_strip()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            Assert.True(rig.Host.IsVisible);
            rig.Advance(500);

            Assert.Equal(HostWindowState.SnappedHidden, rig.State);
            Assert.False(rig.Host.IsVisible);
            Assert.True(rig.Controller.Handle.IsVisible);
            var h = rig.HandleBounds;
            Assert.Equal(1910, h.X);                 // HandleThickness 10 stays on screen
            Assert.Equal(10, h.Width);
            Assert.Equal(HostWindowController.HandleLengthDip, h.Height);
            Assert.Equal(1040 * 0.5, h.Y + h.Height / 2, 0);
        });
    }

    [Fact]
    public void Handle_hover_reveals_and_leaving_early_does_not()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Advance(500);

            rig.Controller.Handle.SimulateHover(true);
            rig.Advance(100);
            rig.Controller.Handle.SimulateHover(false);
            rig.Advance(500);
            Assert.False(rig.Host.IsVisible);

            rig.Controller.Handle.SimulateHover(true);
            rig.Advance(150);
            Assert.Equal(HostWindowState.SnappedVisible, rig.State);
            Assert.True(rig.Host.IsVisible);
            Assert.False(rig.Controller.Handle.IsVisible);
            Assert.Equal(1920, rig.HostBounds.X + rig.HostBounds.Width);
        });
    }

    [Fact]
    public void Owned_window_keeps_the_host_open_and_focus_alone_does_not_hide()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            var owned = new Window { Owner = rig.Host, Width = 100, Height = 100, ShowActivated = false };
            owned.Show();
            rig.Controller.UpdatePointerInside();
            rig.Advance(3000);
            Assert.True(rig.Host.IsVisible);

            owned.Close();
            rig.Controller.UpdatePointerInside();
            rig.Advance(500);
            Assert.False(rig.Host.IsVisible);
        });
    }

    [Fact]
    public void Host_slides_out_past_its_edge_as_a_whole_like_the_reference_widget()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Controller.AnimationsEnabled = true;
            var full = rig.HostBounds;

            rig.Timers.Advance(TimeSpan.FromMilliseconds(500));   // start hiding
            Sta.PumpFor(90);
            var mid = rig.HostBounds;
            Assert.True(mid.X > full.X, "the window travels toward its edge");
            Assert.Equal(full.Width, mid.Width);                  // it is not squeezed: same size, just moving
            Assert.Equal(full.Height, mid.Height);
            Sta.PumpFor(500);
            Assert.False(rig.Host.IsVisible);
            Assert.True(rig.Controller.Handle.IsVisible);
            Assert.Equal(full, rig.Controller.VisibleBounds);     // the visible position is remembered

            rig.Controller.Handle.SimulateHover(true);
            rig.Timers.Advance(TimeSpan.FromMilliseconds(150));
            Sta.PumpFor(500);
            Assert.True(rig.Host.IsVisible);
            Assert.Equal(full, rig.HostBounds);
        });
    }

    [Fact]
    public void With_a_neighbour_monitor_the_host_folds_like_a_curtain_and_never_shows_on_it()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Monitors.List.Add(new MonitorInfo("right", new WidgetRect(1920, 0, 1920, 1040), 1.0, false));
            rig.Controller.AnimationsEnabled = true;
            var full = rig.HostBounds;

            rig.Timers.Advance(TimeSpan.FromMilliseconds(500));
            Sta.PumpFor(90);
            var mid = rig.HostBounds;
            Assert.True(mid.Width < full.Width, "the window narrows instead of travelling");
            Assert.Equal(1920, mid.X + mid.Width);                // never crosses onto the neighbour
            Sta.PumpFor(500);
            Assert.False(rig.Host.IsVisible);
        });
    }

    [Fact]
    public void Neighbour_detection_looks_only_at_monitors_beyond_the_snapped_edge()
    {
        var main = new MonitorInfo("main", new WidgetRect(0, 0, 1920, 1040), 1.0, true);
        var right = new MonitorInfo("right", new WidgetRect(1920, 0, 1920, 1040), 1.0, false);
        var below = new MonitorInfo("below", new WidgetRect(0, 1040, 1920, 1040), 1.0, false);
        var win = new WidgetRect(1620, 200, 300, 500);
        var all = new[] { main, right, below };
        Assert.True(HostWindowController.HasNeighbor(all, main, ScreenEdge.Right, win));
        Assert.False(HostWindowController.HasNeighbor(all, main, ScreenEdge.Left, win));
        Assert.False(HostWindowController.HasNeighbor(all, main, ScreenEdge.Top, win));
        Assert.True(HostWindowController.HasNeighbor(all, main, ScreenEdge.Bottom, new WidgetRect(500, 540, 300, 500)));
        Assert.False(HostWindowController.HasNeighbor(new[] { main }, main, ScreenEdge.Right, win));
        // a monitor that is beside the edge but not next to the window's vertical range does not count
        var farAbove = new MonitorInfo("far", new WidgetRect(1920, -5000, 1920, 1000), 1.0, false);
        Assert.False(HostWindowController.HasNeighbor(new[] { main, farAbove }, main, ScreenEdge.Right, win));
    }

    [Fact]
    public void Revealing_during_the_hide_slide_does_not_leave_a_half_closed_host()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Controller.AnimationsEnabled = true;
            var full = rig.HostBounds;

            rig.Timers.Advance(TimeSpan.FromMilliseconds(500));   // hide slide starts
            Sta.PumpFor(50);
            rig.Controller.Machine.Reveal();                      // user asks for it back mid-slide
            Sta.PumpFor(500);

            Assert.True(rig.Host.IsVisible);
            Assert.False(rig.Controller.Handle.IsVisible);
            Assert.Equal(full.Width, rig.HostBounds.Width);
            Assert.Equal(1920, rig.HostBounds.X + rig.HostBounds.Width);
        });
    }

    [Fact]
    public void Changing_handle_thickness_updates_a_visible_handle()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Advance(500);
            Assert.Equal(10, rig.HandleBounds.Width);
            rig.Controller.Machine.ApplySettings(rig.Controller.Machine.Settings with { HandleThickness = 16 });
            Sta.Pump();
            Assert.Equal(16, rig.HandleBounds.Width);
            Assert.Equal(1904, rig.HandleBounds.X);
        });
    }

    [Fact]
    public void Pressing_the_handle_cancels_the_hover_reveal_and_a_click_reveals()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Advance(500);

            rig.Controller.Handle.SimulateHover(true);     // pointer arrives, reveal timer starts
            rig.Advance(60);
            rig.Controller.Handle.SimulatePress();          // ...and the user grabs the strip
            rig.Advance(1000);
            Assert.False(rig.Host.IsVisible, "the Host must not open underneath a handle that is being held");

            rig.Controller.Handle.SimulateClick();          // released without dragging = click = show now
            Sta.Pump();
            Assert.True(rig.Host.IsVisible);
        });
    }

    [Fact]
    public void Pointer_inside_the_host_including_the_title_bar_keeps_it_visible()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            var b = rig.HostBounds;
            rig.Cursor = new Point(b.X + 50, b.Y + 3); // on the title bar (non-client area)
            rig.Controller.UpdatePointerInside();
            rig.Advance(5000);
            Assert.True(rig.Host.IsVisible);

            rig.Cursor = new Point(100, 100); // moves away
            rig.Controller.UpdatePointerInside();
            rig.Advance(500);
            Assert.False(rig.Host.IsVisible);
        });
    }

    [Fact]
    public void Widget_drag_and_drop_preview_and_popups_prevent_hiding()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Controller.SetWidgetDragActivity(true, false);
            rig.Advance(3000);
            Assert.True(rig.Host.IsVisible);

            rig.Controller.SetWidgetDragActivity(true, true);
            rig.Advance(3000);
            Assert.True(rig.Host.IsVisible);

            rig.Controller.SetWidgetDragActivity(false, false);
            rig.Controller.SetPopupOpen(true);
            rig.Advance(3000);
            Assert.True(rig.Host.IsVisible);

            rig.Controller.SetPopupOpen(false);
            rig.Advance(500);
            Assert.False(rig.Host.IsVisible);
        });
    }

    [Fact]
    public void Real_host_controller_drag_keeps_the_window_visible()
    {
        Sta.Run(() =>
        {
            // HostController drag activity is wired to the placement controller by MainWindow.AttachPlacement.
            var registry = new WidgetRegistry();
            registry.Register(() => new TestWidget("dev.test.multi", true, true, true));
            var hc = new HostController(registry, new InMemoryWidgetStateStore(), new InMemoryLayoutStore()) { UseMouseCapture = false };
            var win = new MainWindow(hc);
            var monitors = new FakeMonitors();
            var timers = new ManualTimerScheduler();
            var placement = new HostWindowController(win, SnappedRight(autoHide: true), monitors, timers) { AnimationsEnabled = false };
            win.AttachPlacement(placement);
            placement.Initialize();
            win.Show();
            try
            {
                hc.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
                hc.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
                Sta.Pump();

                var chrome = hc.Panel.Chromes.Values.First();
                var b = Interop.ScreenBoundsPx(chrome);
                var start = new Point(b.Left + 20, b.Top + 20);
                hc.PressDown(chrome, start, 1);
                hc.PressMove(chrome, new Point(start.X, start.Y + 40), true);
                Assert.True((placement.Machine.Busy & HostBusy.WidgetDragging) != 0);

                timers.Advance(TimeSpan.FromSeconds(5));
                Assert.True(win.IsVisible);

                hc.PressUp(chrome, new Point(start.X, start.Y + 40));
                Assert.Equal(HostBusy.None, placement.Machine.Busy & HostBusy.WidgetDragging);
            }
            finally
            {
                hc.Details.CloseAll();
                hc.Floating.CloseAll();
                placement.Dispose();
                win.Close();
            }
        });
    }

    [Fact]
    public void Context_menu_tracking_marks_the_host_busy()
    {
        Sta.Run(() =>
        {
            var tracker = new HostPopupTracker();
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            tracker.Changed += rig.Controller.SetPopupOpen;

            using (tracker.Scope())
            {
                Assert.True(tracker.AnyOpen);
                rig.Advance(3000);
                Assert.True(rig.Host.IsVisible);
                using (tracker.Scope()) { }
                Assert.True(tracker.AnyOpen); // nested scope closing must not release the outer one
            }
            Assert.False(tracker.AnyOpen);
            rig.Advance(500);
            Assert.False(rig.Host.IsVisible);
        });
    }

    // ---- window move via real WM messages ----

    [Fact]
    public void Dragging_near_the_right_edge_pulls_the_window_flush_and_snaps_on_drop()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(new HostWindowSettings
            {
                MonitorId = "main",
                FloatingBounds = new WidgetRect(600, 200, 300, 500),
                FloatingDpiScale = 1.0
            });
            Assert.Equal(HostWindowState.Floating, rig.State);

            rig.BeginMove();
            Assert.Equal(HostWindowState.Dragging, rig.State);

            var far = rig.Moving(new WidgetRect(900, 200, 300, 500));
            Assert.False(far.Handled);

            // 8 px from the right edge -> magnetic pull to x = 1620.
            var near = rig.Moving(new WidgetRect(1612, 210, 300, 500));
            Assert.True(near.Handled);
            Assert.Equal(1620, near.Rect.X);
            Assert.Equal(210, near.Rect.Y);
            Assert.Equal(HostWindowState.SnapPreview, rig.State);

            rig.EndMove(near.Rect);
            Assert.Equal(HostWindowState.SnappedVisible, rig.State);
            Assert.Equal(ScreenEdge.Right, rig.Controller.Machine.SnappedEdge);
            Assert.True(rig.Host.Topmost);
            Assert.Equal(ScreenEdge.Right, rig.Saved.Last().SnappedEdge);
            Assert.Equal(HostPlacementState.Snapped, rig.Saved.Last().PlacementState);
        });
    }

    [Fact]
    public void Dragging_a_snapped_host_away_floats_it_and_stores_floating_bounds()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight());
            rig.BeginMove();
            var r = rig.Moving(new WidgetRect(900, 300, 300, 500));
            Assert.False(r.Handled);
            rig.EndMove(r.Rect);

            Assert.Equal(HostWindowState.Floating, rig.State);
            Assert.False(rig.Host.Topmost);
            Assert.Equal(new WidgetRect(900, 300, 300, 500), rig.Saved.Last().FloatingBounds);
            Assert.Equal(ScreenEdge.None, rig.Saved.Last().SnappedEdge);
        });
    }

    [Fact]
    public void Resizing_a_snapped_host_keeps_it_flush()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight());
            rig.BeginResize();
            Assert.Equal(HostWindowState.Resizing, rig.State);
            rig.EndMove(new WidgetRect(1500, 100, 420, 600));
            Assert.Equal(HostWindowState.SnappedVisible, rig.State);
            Assert.Equal(1920, rig.HostBounds.X + rig.HostBounds.Width);
            Assert.Equal(420, rig.HostBounds.Width);
        });
    }

    // ---- handle drag ----

    private static Point P(double x, double y) => new(x, y);

    [Fact]
    public void Handle_drag_along_the_edge_moves_the_handle_and_stays_hidden()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Advance(500);
            rig.Controller.Handle.SimulateDrag(P(1915, 520), new[] { P(1915, 600), P(1915, 780) }, P(1915, 780));

            Assert.Equal(HostWindowState.SnappedHidden, rig.State);
            Assert.Equal(ScreenEdge.Right, rig.Controller.Machine.SnappedEdge);
            Assert.Equal(0.75, rig.Controller.Machine.Settings.HandleOffsetRatio, 2);
            var h = rig.HandleBounds;
            Assert.Equal(1910, h.X);
            Assert.Equal(1040 * 0.75, h.Y + h.Height / 2, 0);
            Assert.False(rig.Host.IsVisible);
        });
    }

    [Fact]
    public void Handle_drag_to_the_other_edge_switches_edges()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Advance(500);
            rig.Controller.Handle.SimulateDrag(P(1915, 520), new[] { P(900, 520), P(30, 300) }, P(30, 300));

            Assert.Equal(ScreenEdge.Left, rig.Controller.Machine.SnappedEdge);
            Assert.Equal(HostWindowState.SnappedHidden, rig.State);
            Assert.Equal(0, rig.HandleBounds.X);
            Assert.False(rig.Host.IsVisible);

            // and revealing now slides the Host out of the left edge
            rig.Controller.Handle.SimulateHover(true);
            rig.Advance(150);
            Assert.True(rig.Host.IsVisible);
            Assert.Equal(0, rig.HostBounds.X);
        });
    }

    [Fact]
    public void Handle_drag_to_the_middle_detaches_into_a_floating_host()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Advance(500);
            rig.Controller.Handle.SimulateDrag(P(1915, 520), new[] { P(1200, 520), P(960, 500) }, P(960, 500));

            Assert.Equal(HostWindowState.Floating, rig.State);
            Assert.True(rig.Host.IsVisible);
            Assert.False(rig.Controller.Handle.IsVisible);
            Assert.False(rig.Host.Topmost);
            var b = rig.HostBounds;
            Assert.Equal(960, b.X + b.Width / 2, 0);
            Assert.Equal(HostPlacementState.Floating, rig.Saved.Last().PlacementState);
        });
    }

    // ---- monitors / DPI ----

    [Fact]
    public void Missing_monitor_falls_back_to_the_primary_one()
    {
        Sta.Run(() =>
        {
            var s = SnappedRight() with { MonitorId = "unplugged" };
            using var rig = new PlacementRig(s);
            Assert.Equal(1920, rig.HostBounds.X + rig.HostBounds.Width);
        });
    }

    [Fact]
    public void Display_change_replaces_a_snapped_host_on_the_new_layout()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight());
            // resolution shrinks to 1280x720 (taskbar 40)
            rig.Monitors.List = new() { new MonitorInfo("main", new WidgetRect(0, 0, 1280, 680), 1.0, true) };
            rig.Controller.Reapply();
            Assert.Equal(1280, rig.HostBounds.X + rig.HostBounds.Width);
            Assert.True(rig.HostBounds.Y + rig.HostBounds.Height <= 680);
        });
    }

    [Fact]
    public void Dpi_change_rescales_the_snapped_host_size()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight());
            rig.Monitors.List = new() { new MonitorInfo("main", new WidgetRect(0, 0, 2880, 1560), 1.5, true) };
            rig.Controller.Reapply();
            var b = rig.HostBounds;
            Assert.Equal(450, b.Width);   // 300 DIP at 150%
            Assert.Equal(2880, b.X + b.Width);
        });
    }

    [Fact]
    public void Floating_host_is_pulled_back_when_its_monitor_is_gone()
    {
        Sta.Run(() =>
        {
            var s = new HostWindowSettings
            {
                MonitorId = "main",
                FloatingBounds = new WidgetRect(400, 200, 300, 500),
                FloatingDpiScale = 1.0
            };
            using var rig = new PlacementRig(s);
            rig.Monitors.List = new() { new MonitorInfo("main", new WidgetRect(0, 0, 640, 480), 1.0, true) };
            rig.Controller.Reapply();
            var b = rig.HostBounds;
            Assert.True(b.X >= 0 && b.X + b.Width <= 640);
            Assert.True(b.Y >= 0 && b.Y + b.Height <= 480);
        });
    }

    // ---- settings ----

    [Fact]
    public void Turning_snap_off_releases_the_host_and_persists()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Advance(500);
            Assert.False(rig.Host.IsVisible);
            rig.Controller.Machine.ApplySettings(rig.Controller.Machine.Settings with { SnapEnabled = false });
            Sta.Pump();
            Assert.Equal(HostWindowState.Floating, rig.State);
            Assert.True(rig.Host.IsVisible);
            Assert.False(rig.Controller.Handle.IsVisible);
            Assert.False(rig.Saved.Last().SnapEnabled);
        });
    }

    [Fact]
    public void Turning_auto_hide_off_brings_a_hidden_host_back()
    {
        Sta.Run(() =>
        {
            using var rig = new PlacementRig(SnappedRight(autoHide: true));
            rig.Advance(500);
            rig.Controller.Machine.ApplySettings(rig.Controller.Machine.Settings with { AutoHideEnabled = false });
            Sta.Pump();
            Assert.True(rig.Host.IsVisible);
            Assert.Equal(1920, rig.HostBounds.X + rig.HostBounds.Width);
        });
    }

    [Fact]
    public void Widget_attention_event_lights_the_handle_until_the_host_is_revealed()
    {
        Sta.Run(() =>
        {
            var registry = new WidgetRegistry();
            registry.Register(() => new TestWidget("dev.test.multi", true, true, true));
            var hc = new HostController(registry, new InMemoryWidgetStateStore(), new InMemoryLayoutStore()) { UseMouseCapture = false };
            var win = new MainWindow(hc);
            var timers = new ManualTimerScheduler();
            var placement = new HostWindowController(win, SnappedRight(autoHide: true), new FakeMonitors(), timers) { AnimationsEnabled = false };
            win.AttachPlacement(placement);
            placement.Initialize();
            win.Show();
            try
            {
                timers.Advance(TimeSpan.FromMilliseconds(500));
                Assert.False(placement.Handle.Indicator);
                hc.Runtime.Events.Publish(HostTopics.Attention, true);
                Sta.Pump();
                Assert.True(placement.Handle.Indicator);

                placement.Handle.SimulateHover(true);
                timers.Advance(TimeSpan.FromMilliseconds(150));
                Assert.False(placement.Handle.Indicator);
            }
            finally
            {
                placement.Dispose();
                win.Close();
            }
        });
    }

    [Fact]
    public void Widget_state_survives_hide_and_reveal()
    {
        Sta.Run(() =>
        {
            var registry = new WidgetRegistry();
            registry.Register(() => new TestWidget("dev.test.multi", true, true, true));
            var hc = new HostController(registry, new InMemoryWidgetStateStore(), new InMemoryLayoutStore()) { UseMouseCapture = false };
            var win = new MainWindow(hc);
            var timers = new ManualTimerScheduler();
            var placement = new HostWindowController(win, SnappedRight(autoHide: true), new FakeMonitors(), timers) { AnimationsEnabled = false };
            win.AttachPlacement(placement);
            placement.Initialize();
            win.Show();
            try
            {
                hc.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
                Sta.Pump();
                var chrome = hc.Panel.Chromes.Values.First();
                hc.TogglePin(chrome.InstanceId);
                hc.OpenDetail(chrome.InstanceId);
                var before = (chrome.Instance.State.InteractionState, chrome.Instance.State.DockState, chrome.Instance.State.IsDetailOpen);

                timers.Advance(TimeSpan.FromMilliseconds(500));
                Assert.False(win.IsVisible);
                placement.Handle.SimulateHover(true);
                timers.Advance(TimeSpan.FromMilliseconds(150));
                Assert.True(win.IsVisible);

                var after = (chrome.Instance.State.InteractionState, chrome.Instance.State.DockState, chrome.Instance.State.IsDetailOpen);
                Assert.Equal(before, after);
                Assert.Equal(WidgetInteractionState.Pinned, after.InteractionState);
                Assert.True(hc.Details.IsOpen(chrome.InstanceId));
            }
            finally
            {
                hc.Details.CloseAll();
                hc.Floating.CloseAll();
                placement.Dispose();
                win.Close();
            }
        });
    }
}
