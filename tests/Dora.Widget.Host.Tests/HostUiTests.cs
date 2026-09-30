using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dora.Widget.Abstractions;
using Dora.Widget.Host;
using Dora.Widget.Host.HostWindow;
using Dora.Widget.Runtime.HostWindow;
using Dora.Widget.Runtime;

namespace Dora.Widget.Host.Tests;

internal sealed class TestWidget : IComposableWidget, IDisplayModeAware
{
    public IWidgetContext? Context;

    public TestWidget(string id, bool multi, bool detail, bool floating, WidgetCapabilities caps = WidgetCapabilities.None,
        double minCollapsedWidth = 40)
    {
        Manifest = new WidgetManifest
        {
            Id = id,
            Name = id,
            Version = new Version(1, 0),
            ContractVersion = ContractInfo.Current,
            Layout = new WidgetLayoutProfile
            {
                NaturalSize = new(Math.Max(200, minCollapsedWidth), 100),
                CompactSize = new(Math.Max(200, minCollapsedWidth), 40),
                CollapsedSize = new(Math.Max(200, minCollapsedWidth), 20),
                MinNaturalSize = new(100, 100),
                MinCompactSize = new(80, 40),
                MinCollapsedSize = new(minCollapsedWidth, 20)
            },
            Capabilities = caps,
            AllowMultipleInstances = multi,
            SupportsDetailView = detail,
            SupportsFloating = floating
        };
    }

    public WidgetManifest Manifest { get; }
    public WidgetDisplayMode Mode = WidgetDisplayMode.Compact;
    public int Counter;

    public Task InitializeAsync(IWidgetContext context, CancellationToken ct) { Context = context; return Task.CompletedTask; }
    public object CreateSummaryView(IWidgetContext context) => new ModeView(this);
    public object? CreateDetailView(IWidgetContext context) => Manifest.SupportsDetailView ? new TextBlock { Text = "detail" } : null;
    public Task SaveStateAsync(IWidgetStateWriter writer) { writer.Write(1, Counter.ToString()); return Task.CompletedTask; }
    public Task RestoreStateAsync(IWidgetStateReader reader)
    {
        if (reader.TryRead(out _, out var json)) Counter = int.Parse(json);
        return Task.CompletedTask;
    }
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    public void OnDisplayModeChanged(WidgetDisplayMode mode) => Mode = mode;
}

internal sealed class ModeView : TextBlock, IDisplayModeAware
{
    private readonly TestWidget _owner;
    public ModeView(TestWidget owner) { _owner = owner; Text = owner.Manifest.Name; }
    public void OnDisplayModeChanged(WidgetDisplayMode mode) => _owner.Mode = mode;
}

/// <summary>Runs a test body on an STA thread with a WPF dispatcher.</summary>
internal static class Sta
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>
    /// Real mouse events reach test windows that happen to sit under the physical cursor (hover, handle reveal, ...)
    /// and change results. No test window is ever placed in the bottom-left corner, so park the cursor there.
    /// </summary>
    private static void ParkCursor() => SetCursorPos(0, Math.Max(0, GetSystemMetrics(1) - 1));

    public static void Run(Action body)
    {
        ParkCursor();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Exception("STA test failed: " + error, error);
    }

    /// <summary>Lets real-time work (animations) run for the given time.</summary>
    public static void PumpFor(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}

internal sealed class Rig : IDisposable
{
    public readonly InMemoryWidgetStateStore States;
    public readonly InMemoryLayoutStore Layouts;
    public readonly List<string> Logs = new();
    public HostController Controller = null!;
    public Window Window = null!;

    public Rig(InMemoryWidgetStateStore? states = null, InMemoryLayoutStore? layouts = null)
    {
        States = states ?? new InMemoryWidgetStateStore();
        Layouts = layouts ?? new InMemoryLayoutStore();
        Build();
    }

    public void Build()
    {
        var registry = new WidgetRegistry();
        registry.Register(() => new TestWidget("dev.test.multi", multi: true, detail: true, floating: true));
        registry.Register(() => new TestWidget("dev.test.fixed", multi: false, detail: false, floating: false));
        Controller = new HostController(registry, States, Layouts, log: Logs.Add) { UseMouseCapture = false };
        Controller.Panel.AnimationsEnabled = false;
        Window = new Window
        {
            Left = 100, Top = 100, Width = 300, Height = 500,
            ShowActivated = false,
            Content = Controller.Panel
        };
        Window.Show();
        Sta.Pump();
    }

    public WidgetChrome Chrome(int dockIndex) =>
        Controller.Panel.Chromes[Controller.Model.DockedOrder[dockIndex]];

    public WidgetRuntimeState State(WidgetChrome c) => c.Instance.State;

    public System.Windows.Point ScreenPointOn(WidgetChrome c, double dx = 10, double dy = 10)
    {
        var b = Interop.ScreenBoundsPx(c);
        return new System.Windows.Point(b.Left + dx, b.Top + dy);
    }

    public void Dispose()
    {
        Controller.Details.CloseAll();
        Controller.Floating.CloseAll();
        Window.Close();
    }
}

public class HostUiTests
{
    [Fact]
    public void Permission_prompt_grants_once_and_is_remembered_across_restarts()
    {
        Sta.Run(() =>
        {
            var grants = new InMemoryPermissionGrantStore();
            var prompts = new List<string>();
            HostController Make()
            {
                var reg = new WidgetRegistry();
                reg.Register(() => new TestWidget("dev.test.fs", false, false, true, WidgetCapabilities.FileSystem));
                return new HostController(reg, new InMemoryWidgetStateStore(), new InMemoryLayoutStore(), grants: grants)
                {
                    PermissionPrompt = (m, caps) => { prompts.Add(m.Id + ":" + caps); return Task.FromResult(true); }
                };
            }

            var first = Make();
            first.AddWidgetAsync("dev.test.fs").GetAwaiter().GetResult();
            var ctx = ((TestWidget)first.Runtime.Instances.Single().Widget).Context!;
            Assert.False(ctx.Permissions.IsGranted(WidgetCapabilities.FileSystem));
            Assert.True(ctx.Permissions.RequestAsync(WidgetCapabilities.FileSystem).GetAwaiter().GetResult());
            Assert.Single(prompts);
            Assert.Equal(WidgetCapabilities.FileSystem, grants.Get("dev.test.fs"));
            // undeclared capabilities are refused without ever asking the user
            Assert.False(ctx.Permissions.RequestAsync(WidgetCapabilities.Network).GetAwaiter().GetResult());
            Assert.Single(prompts);

            var second = Make();
            second.AddWidgetAsync("dev.test.fs").GetAwaiter().GetResult();
            var ctx2 = ((TestWidget)second.Runtime.Instances.Single().Widget).Context!;
            Assert.True(ctx2.Permissions.IsGranted(WidgetCapabilities.FileSystem));
            Assert.True(ctx2.Permissions.RequestAsync(WidgetCapabilities.FileSystem).GetAwaiter().GetResult());
            Assert.Single(prompts);
        });
    }

    [Fact]
    public void Host_window_cannot_become_narrower_than_a_widgets_declared_minimum()
    {
        Sta.Run(() =>
        {
            var reg = new WidgetRegistry();
            reg.Register(() => new TestWidget("dev.test.wide", false, false, true, minCollapsedWidth: 260));
            reg.Register(() => new TestWidget("dev.test.multi", true, true, true));
            var hc = new HostController(reg, new InMemoryWidgetStateStore(), new InMemoryLayoutStore()) { UseMouseCapture = false };
            var win = new MainWindow(hc) { Width = 400, Height = 400, ShowActivated = false };
            win.Show();
            try
            {
                hc.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
                Sta.Pump();
                Assert.Equal(MainWindow.BaseMinWidth, win.MinWidth);   // small widgets: the base minimum

                hc.AddWidgetAsync("dev.test.wide").GetAwaiter().GetResult();
                Sta.Pump();
                Assert.True(win.MinWidth >= 260 + WidgetHostPanel.WidthOverhead, $"MinWidth was {win.MinWidth}");

                hc.RemoveWidgetAsync(hc.Panel.Chromes.Values.Single(c => c.Instance.State.WidgetId == "dev.test.wide").InstanceId)
                    .GetAwaiter().GetResult();
                Sta.Pump();
                Assert.Equal(MainWindow.BaseMinWidth, win.MinWidth);   // and it relaxes again
            }
            finally
            {
                hc.Details.CloseAll();
                hc.Floating.CloseAll();
                win.Close();
            }
        });
    }

    [Fact]
    public void Added_widgets_are_stacked_in_compact_mode()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            for (var i = 0; i < 3; i++) rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();

            Assert.Equal(3, rig.Controller.Model.DockedOrder.Count);
            var tops = Enumerable.Range(0, 3).Select(i => Canvas.GetTop(rig.Chrome(i))).ToArray();
            Assert.Equal(0, tops[0]);
            Assert.True(tops[1] > tops[0] && tops[2] > tops[1]);
            Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(40, rig.Chrome(i).Height));
        });
    }

    [Fact]
    public void First_widget_sits_at_the_top_of_the_host_and_layout_maps_to_screen()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            for (var i = 0; i < 2; i++) rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();

            var panel = Interop.ScreenBoundsPx(rig.Controller.Panel);
            var first = Interop.ScreenBoundsPx(rig.Chrome(0));
            var second = Interop.ScreenBoundsPx(rig.Chrome(1));
            Assert.Equal(panel.Top, first.Top, 1);
            Assert.Equal(first.Bottom + 4, second.Top, 1); // ItemSpacing
            Assert.Equal(first.Top, panel.Top + Canvas.GetTop(rig.Chrome(0)), 1);
            // Pointer Y maps back to the same canvas coordinate.
            Assert.Equal(Canvas.GetTop(rig.Chrome(1)), rig.Controller.Panel.ScreenYToCanvas(second.Top), 1);
        });
    }

    [Fact]
    public void Pin_expands_widget_and_notifies_display_mode()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            for (var i = 0; i < 2; i++) rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();

            var first = rig.Chrome(0);
            rig.Controller.TogglePin(first.InstanceId);
            Sta.Pump();

            Assert.Equal(WidgetInteractionState.Pinned, rig.State(first).InteractionState);
            Assert.Equal(100, first.Height);
            Assert.Equal(WidgetDisplayMode.Natural, ((TestWidget)first.Instance.Widget).Mode);
            Assert.Equal(40, rig.Chrome(1).Height);

            rig.Controller.TogglePin(first.InstanceId);
            Sta.Pump();
            Assert.Equal(40, first.Height);
        });
    }

    [Fact]
    public void Float_and_redock_from_menu_keep_same_summary_view()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();
            var chrome = rig.Chrome(0);
            var view = chrome.SummaryView;

            rig.Controller.ToggleFloat(chrome.InstanceId);
            Sta.Pump();
            Assert.Equal(DockState.Floating, rig.State(chrome).DockState);
            Assert.True(rig.Controller.Floating.Contains(chrome.InstanceId));
            Assert.Same(chrome, rig.Controller.Floating.Get(chrome.InstanceId)!.Content);
            Assert.Empty(rig.Controller.Model.DockedOrder);
            Assert.Equal(200, chrome.Width);
            Assert.Equal(100, chrome.Height);

            rig.Controller.ToggleFloat(chrome.InstanceId);
            Sta.Pump();
            Assert.Equal(DockState.Docked, rig.State(chrome).DockState);
            Assert.False(rig.Controller.Floating.Contains(chrome.InstanceId));
            Assert.Same(view, chrome.SummaryView);
            Assert.Single(rig.Controller.Model.DockedOrder);
        });
    }

    [Fact]
    public void Redocked_widget_appears_at_its_slot_without_sliding_in()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            for (var i = 0; i < 2; i++) rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();
            rig.Controller.Panel.AnimationsEnabled = true;

            var first = rig.Chrome(0);
            rig.Controller.ToggleFloat(first.InstanceId); // its old Canvas.Top was 0
            Sta.Pump();
            rig.Controller.ToggleFloat(first.InstanceId); // re-docked at the end: slot top is 44
            Sta.Pump();

            Assert.Equal(44, Canvas.GetTop(first), 1);
            Assert.False(first.HasAnimatedProperties);
        });
    }

    [Fact]
    public void Non_floating_widget_cannot_be_detached()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            rig.Controller.AddWidgetAsync("dev.test.fixed").GetAwaiter().GetResult();
            Sta.Pump();
            var chrome = rig.Chrome(0);
            rig.Controller.ToggleFloat(chrome.InstanceId);
            Assert.Equal(DockState.Docked, rig.State(chrome).DockState);
            Assert.False(rig.Controller.Floating.Contains(chrome.InstanceId));
        });
    }

    [Fact]
    public void Drag_reorders_and_keeps_pin()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            for (var i = 0; i < 3; i++) rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();

            var a = rig.Chrome(0);
            var idA = a.InstanceId;
            rig.Controller.TogglePin(idA);
            Sta.Pump();

            var start = rig.ScreenPointOn(a, 20, 20);
            rig.Controller.PressDown(a, start, 1);
            // Move far below the last widget, staying inside the host.
            var end = new System.Windows.Point(start.X, Interop.ScreenBoundsPx(rig.Controller.Panel).Bottom - 5);
            rig.Controller.PressMove(a, new System.Windows.Point(start.X, start.Y + 40), true);
            Assert.Equal(WidgetInteractionState.Dragging, rig.State(a).InteractionState);
            rig.Controller.PressMove(a, end, true);
            rig.Controller.PressUp(a, end);
            Sta.Pump();

            Assert.Equal(idA, rig.Controller.Model.DockedOrder[^1]);
            Assert.Equal(WidgetInteractionState.Pinned, rig.State(a).InteractionState);
            Assert.Equal(DockState.Docked, rig.State(a).DockState);
        });
    }

    [Fact]
    public void Dragging_upwards_opens_a_gap_before_drop()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            for (var i = 0; i < 3; i++) rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();
            var panel = Interop.ScreenBoundsPx(rig.Controller.Panel);
            var first = rig.Chrome(0);
            var second = rig.Chrome(1);
            var last = rig.Chrome(2);
            var lastId = last.InstanceId;

            var start = rig.ScreenPointOn(last, 20, 20);
            rig.Controller.PressDown(last, start, 1);
            rig.Controller.PressMove(last, new System.Windows.Point(start.X, start.Y - 40), true);
            // Pointer at the very top of the Host -> insertion slot 0.
            var top = new System.Windows.Point(start.X, panel.Top + 2);
            rig.Controller.PressMove(last, top, true);

            // The dragged widget must be drawn above every other widget and above the gap marker.
            Assert.True(Canvas.GetZIndex(last) > Canvas.GetZIndex(first));
            Assert.True(Canvas.GetZIndex(last) > Canvas.GetZIndex(rig.Controller.Panel.GapMarker));

            // Before dropping, the others have already slid down to make room: the dragged widget is
            // focused (natural, 100 high) + 4 spacing.
            Assert.Equal(104, Canvas.GetTop(first), 1);
            Assert.Equal(148, Canvas.GetTop(second), 1);
            Assert.Equal(WidgetInteractionState.Dragging, rig.State(last).InteractionState);

            rig.Controller.PressUp(last, top);
            Sta.Pump();
            Assert.Equal(lastId, rig.Controller.Model.DockedOrder[0]);
            Assert.Equal(0, Canvas.GetTop(last), 1);
            Assert.Equal(104, Canvas.GetTop(first), 1);
            Assert.Equal(148, Canvas.GetTop(second), 1);
        });
    }

    [Fact]
    public void Gap_closes_when_drag_leaves_the_host()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            for (var i = 0; i < 3; i++) rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();
            var panel = Interop.ScreenBoundsPx(rig.Controller.Panel);
            var first = rig.Chrome(0);
            var last = rig.Chrome(2);
            var start = rig.ScreenPointOn(last, 20, 20);
            rig.Controller.PressDown(last, start, 1);
            rig.Controller.PressMove(last, new System.Windows.Point(start.X, start.Y - 40), true);
            rig.Controller.PressMove(last, new System.Windows.Point(start.X, panel.Top + 2), true);
            Assert.Equal(104, Canvas.GetTop(first), 1);

            rig.Controller.PressMove(last, new System.Windows.Point(panel.Right + 300, panel.Top + 2), true);
            Assert.Equal(0, Canvas.GetTop(first), 1);
        });
    }

    [Fact]
    public void Drag_outside_detaches_and_drag_back_redocks()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            for (var i = 0; i < 2; i++) rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();

            var a = rig.Chrome(0);
            var host = Interop.ScreenBoundsPx(rig.Controller.Panel);
            var start = rig.ScreenPointOn(a, 20, 20);
            var outside = new System.Windows.Point(host.Right + 300, host.Top + 50);

            rig.Controller.PressDown(a, start, 1);
            rig.Controller.PressMove(a, new System.Windows.Point(start.X + 40, start.Y), true);
            rig.Controller.PressMove(a, outside, true);
            rig.Controller.PressUp(a, outside);
            Sta.Pump();

            var s = rig.State(a);
            Assert.Equal(DockState.Floating, s.DockState);
            Assert.Equal(WidgetInteractionState.Focused, s.InteractionState);
            Assert.True(rig.Controller.Floating.Contains(a.InstanceId));
            Assert.Single(rig.Controller.Model.DockedOrder);
            Assert.NotNull(s.FloatingBounds);

            // Drag the floating widget back over the Host.
            var inside = new System.Windows.Point(host.Left + 30, host.Top + 5);
            var floatStart = rig.ScreenPointOn(a, 15, 15);
            rig.Controller.PressDown(a, floatStart, 1);
            rig.Controller.PressMove(a, new System.Windows.Point(floatStart.X + 40, floatStart.Y), true);
            rig.Controller.PressMove(a, inside, true);
            rig.Controller.PressUp(a, inside);
            Sta.Pump();

            Assert.Equal(DockState.Docked, s.DockState);
            Assert.Equal(2, rig.Controller.Model.DockedOrder.Count);
            Assert.False(rig.Controller.Floating.Contains(a.InstanceId));
            Assert.Equal(a.InstanceId, rig.Controller.Model.DockedOrder[0]);
        });
    }

    [Fact]
    public void Detach_ghost_shows_only_while_dragging_outside()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            rig.Controller.AddWidgetAsync("dev.test.fixed").GetAwaiter().GetResult();
            Sta.Pump();
            var host = Interop.ScreenBoundsPx(rig.Controller.Panel);
            var outside = new System.Windows.Point(host.Right + 300, host.Top + 50);

            var a = rig.Chrome(0); // floatable
            var start = rig.ScreenPointOn(a, 20, 20);
            rig.Controller.PressDown(a, start, 1);
            rig.Controller.PressMove(a, new System.Windows.Point(start.X + 40, start.Y), true);
            Assert.False(rig.Controller.GhostVisible);
            rig.Controller.PressMove(a, outside, true);
            Assert.True(rig.Controller.GhostVisible);
            rig.Controller.PressMove(a, new System.Windows.Point(start.X + 40, start.Y), true);
            Assert.False(rig.Controller.GhostVisible);
            rig.Controller.PressMove(a, outside, true);
            rig.Controller.PressUp(a, outside);
            Assert.False(rig.Controller.GhostVisible);

            // A widget that cannot float never shows a detach ghost and snaps back.
            var f = rig.Chrome(0);
            Assert.Equal("dev.test.fixed", f.Instance.State.WidgetId);
            var fs = rig.ScreenPointOn(f, 20, 20);
            rig.Controller.PressDown(f, fs, 1);
            rig.Controller.PressMove(f, new System.Windows.Point(fs.X + 40, fs.Y), true);
            rig.Controller.PressMove(f, outside, true);
            Assert.False(rig.Controller.GhostVisible);
            Assert.True(rig.Controller.Panel.BlockedHintVisible);
            Assert.Same(System.Windows.Input.Cursors.No, System.Windows.Input.Mouse.OverrideCursor);
            rig.Controller.PressMove(f, fs, true); // back inside: hint goes away
            Assert.False(rig.Controller.Panel.BlockedHintVisible);
            Assert.Null(System.Windows.Input.Mouse.OverrideCursor);
            rig.Controller.PressMove(f, outside, true);
            rig.Controller.PressUp(f, outside);
            Assert.False(rig.Controller.Panel.BlockedHintVisible);
            Assert.Null(System.Windows.Input.Mouse.OverrideCursor);
            Assert.Equal(DockState.Docked, rig.State(f).DockState);
        });
    }

    [Fact]
    public void Small_movement_is_a_click_not_a_drag()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();
            var a = rig.Chrome(0);
            var p = rig.ScreenPointOn(a);
            rig.Controller.PressDown(a, p, 1);
            rig.Controller.PressMove(a, new System.Windows.Point(p.X + 2, p.Y + 1), true);
            rig.Controller.PressUp(a, new System.Windows.Point(p.X + 2, p.Y + 1));
            Assert.Equal(WidgetInteractionState.Focused, rig.State(a).InteractionState);
        });
    }

    [Fact]
    public void Double_click_opens_single_detail_window_without_changing_dock_or_pin()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();
            var a = rig.Chrome(0);
            rig.Controller.TogglePin(a.InstanceId);

            // The detail opens when the second press is released without having become a drag.
            rig.Controller.PressDown(a, rig.ScreenPointOn(a), 2);
            Assert.False(rig.Controller.Details.IsOpen(a.InstanceId));
            rig.Controller.PressUp(a, rig.ScreenPointOn(a));
            rig.Controller.PressDown(a, rig.ScreenPointOn(a), 2);
            rig.Controller.PressUp(a, rig.ScreenPointOn(a));
            Assert.True(rig.State(a).IsDetailOpen);
            Assert.True(rig.Controller.Details.IsOpen(a.InstanceId));
            Assert.Equal(WidgetInteractionState.Pinned, rig.State(a).InteractionState);

            rig.Controller.Details.Close(a.InstanceId);
            Sta.Pump();
            Assert.False(rig.State(a).IsDetailOpen);
            Assert.Equal(WidgetInteractionState.Pinned, rig.State(a).InteractionState);
            Assert.Equal(DockState.Docked, rig.State(a).DockState);
        });
    }

    [Fact]
    public void Widget_without_detail_view_ignores_double_click()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            rig.Controller.AddWidgetAsync("dev.test.fixed").GetAwaiter().GetResult();
            Sta.Pump();
            var a = rig.Chrome(0);
            rig.Controller.PressDown(a, rig.ScreenPointOn(a), 2);
            Assert.False(rig.State(a).IsDetailOpen);
        });
    }

    [Fact]
    public void Layout_and_widget_state_survive_restart()
    {
        Sta.Run(() =>
        {
            var states = new InMemoryWidgetStateStore();
            var layouts = new InMemoryLayoutStore();
            string idFloating, idPinned;
            using (var rig = new Rig(states, layouts))
            {
                for (var i = 0; i < 3; i++) rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
                Sta.Pump();
                idPinned = rig.Chrome(2).InstanceId;
                idFloating = rig.Chrome(0).InstanceId;
                ((TestWidget)rig.Chrome(1).Instance.Widget).Counter = 42;
                rig.Controller.TogglePin(idPinned);
                rig.Controller.ToggleFloat(idFloating);
                rig.Controller.ShutdownAsync().GetAwaiter().GetResult();
            }

            using var again = new Rig(states, layouts);
            again.Controller.RestoreAsync().GetAwaiter().GetResult();
            Sta.Pump();

            Assert.True(again.Controller.Floating.Contains(idFloating));
            Assert.Equal(2, again.Controller.Model.DockedOrder.Count);
            Assert.Equal(WidgetInteractionState.Pinned, again.Controller.Runtime.Find(idPinned)!.State.InteractionState);
            var counters = again.Controller.Runtime.Instances.Select(i => ((TestWidget)i.Widget).Counter).OrderBy(x => x);
            Assert.Contains(42, counters);
        });
    }

    [Fact]
    public void Empty_saved_layout_starts_with_default_widgets()
    {
        Sta.Run(() =>
        {
            var layouts = new InMemoryLayoutStore();
            layouts.Save(new LayoutSnapshot(LayoutSnapshot.CurrentSchema, null, Array.Empty<LayoutEntry>()));
            using var rig = new Rig(layouts: layouts);
            rig.Controller.RestoreAsync().GetAwaiter().GetResult();
            Sta.Pump();
            Assert.Equal(2, rig.Controller.Model.DockedOrder.Count);
        });
    }

    [Fact]
    public void Entries_of_uninstalled_widgets_are_kept_when_saving()
    {
        Sta.Run(() =>
        {
            var layouts = new InMemoryLayoutStore();
            layouts.Save(new LayoutSnapshot(LayoutSnapshot.CurrentSchema, null, new[]
            {
                new LayoutEntry("ghost-1", "dev.missing.widget", DockState.Docked, 0, false, null),
                new LayoutEntry("keep-1", "dev.test.multi", DockState.Docked, 1, false, null)
            }));
            using var rig = new Rig(layouts: layouts);
            rig.Controller.RestoreAsync().GetAwaiter().GetResult();
            rig.Controller.SaveLayoutNow();
            Assert.Contains(layouts.Load()!.Entries, e => e.InstanceId == "ghost-1");
            Assert.Contains(layouts.Load()!.Entries, e => e.InstanceId == "keep-1");
            Assert.Single(rig.Controller.Model.DockedOrder);
        });
    }

    [Fact]
    public void Floating_widget_is_restored_at_the_dpi_size_of_its_monitor_and_inside_its_work_area()
    {
        Sta.Run(() =>
        {
            var layouts = new InMemoryLayoutStore();
            layouts.Save(new LayoutSnapshot(LayoutSnapshot.CurrentSchema, null, new[]
            {
                new LayoutEntry("f-1", "dev.test.multi", DockState.Floating, 0, false, new WidgetRect(100, 100, 1, 1)),
                new LayoutEntry("f-2", "dev.test.fixed2", DockState.Floating, 1, false, new WidgetRect(99999, 99999, 1, 1))
            }));
            var reg = new WidgetRegistry();
            reg.Register(() => new TestWidget("dev.test.multi", true, true, true));
            reg.Register(() => new TestWidget("dev.test.fixed2", true, true, true));
            // a 200 % monitor: a 200x100 DIP widget must be 400x200 physical pixels
            var monitors = new FakeMonitors
            {
                List = new() { new MonitorInfo("m", new WidgetRect(0, 0, 3840, 2080), 2.0, true) }
            };
            var hc = new HostController(reg, new InMemoryWidgetStateStore(), layouts, monitors: monitors) { UseMouseCapture = false };
            var host = new Window { Content = hc.Panel, Width = 300, Height = 400, ShowActivated = false };
            host.Show();
            try
            {
                hc.RestoreAsync().GetAwaiter().GetResult();
                Sta.Pump();

                var w1 = Native.GetBoundsPx(Native.Handle(hc.Floating.Get("f-1")!));
                Assert.Equal(400, w1.Width);
                Assert.Equal(200, w1.Height);
                Assert.Equal(100, w1.X);

                var w2 = Native.GetBoundsPx(Native.Handle(hc.Floating.Get("f-2")!));
                Assert.True(w2.X + w2.Width <= 3840 && w2.Y + w2.Height <= 2080, "off-screen position must be pulled back");
                Assert.True(w2.X >= 0 && w2.Y >= 0);
            }
            finally
            {
                hc.Floating.CloseAll();
                host.Close();
            }
        });
    }

    [Fact]
    public void Closing_floating_window_redocks_instead_of_destroying()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();
            var a = rig.Chrome(0);
            rig.Controller.ToggleFloat(a.InstanceId);
            rig.Controller.Floating.Get(a.InstanceId)!.Close();
            Sta.Pump();
            Assert.Equal(DockState.Docked, rig.State(a).DockState);
            Assert.Single(rig.Controller.Model.DockedOrder);
            Assert.NotNull(rig.Controller.Runtime.Find(a.InstanceId));
        });
    }

    [Fact]
    public void Remove_deletes_instance_and_closes_windows()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            var a = rig.Chrome(0);
            var id = a.InstanceId;
            rig.Controller.OpenDetail(id);
            rig.Controller.RemoveWidgetAsync(id).GetAwaiter().GetResult();
            Sta.Pump();
            Assert.Null(rig.Controller.Runtime.Find(id));
            Assert.False(rig.Controller.Details.IsOpen(id));
            Assert.Empty(rig.Controller.Model.DockedOrder);
            Assert.Empty(rig.Controller.Panel.Chromes);
        });
    }

    [Fact]
    public void Narrow_or_short_host_downgrades_and_scrolls()
    {
        Sta.Run(() =>
        {
            using var rig = new Rig();
            for (var i = 0; i < 30; i++) rig.Controller.AddWidgetAsync("dev.test.multi").GetAwaiter().GetResult();
            Sta.Pump();
            var layout = rig.Controller.Model.LastLayout!;
            Assert.True(layout.NeedsScrolling);
            Assert.Contains(layout.Slots, s => s.Mode == WidgetDisplayMode.Collapsed);
        });
    }
}
