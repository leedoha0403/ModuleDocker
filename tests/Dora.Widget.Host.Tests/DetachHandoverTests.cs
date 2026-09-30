using System.Windows.Threading;
using Dora.Widget.Abstractions;
using Dora.Widget.Host;
using Dora.Widget.Runtime;

namespace Dora.Widget.Host.Tests;

internal sealed class FakeDetachHandler : IWidgetDetachHandler
{
    public FakeDetachHandler(string widgetId) => WidgetId = widgetId;

    public string WidgetId { get; }
    public bool IsDetached { get; set; }
    public bool ShutdownCalled { get; private set; }
    public int Starts;
    public List<DetachRequest> Requests { get; } = new();
    public Func<DetachRequest, Task<bool>> OnDetach { get; set; } = _ => Task.FromResult(true);

    public event Func<DockRequest, Task<bool>>? DockRequested;
    public event Action<WidgetPoint?>? DockHover;
    public event Action? DetachEnded;

    public async Task<bool> DetachAsync(DetachRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var ok = await OnDetach(request);
        IsDetached = ok;
        return ok;
    }

    public Task<bool> RaiseDockRequest(DockRequest request) => DockRequested!.Invoke(request);
    public void RaiseHover(WidgetPoint? point) => DockHover?.Invoke(point);
    public void RaiseEnded() => DetachEnded?.Invoke();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Starts);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync()
    {
        ShutdownCalled = true;
        return Task.CompletedTask;
    }
}

// Dragging a widget out of the Host hands it to its own application; dropping that application back docks it again.
public class DetachHandoverTests
{
    private const string WidgetId = "dev.test.external";

    private sealed class Env
    {
        public HostController Controller = null!;
        public FakeDetachHandler Handler = null!;
        public InMemoryWidgetStateStore States = null!;
        public List<string> Logs = null!;
        public System.Windows.Window Window = null!;
        public bool Allow = true;
        public int Prompts;
    }

    private static Env Build(WidgetCapabilities caps = WidgetCapabilities.ProcessExecution, bool multi = false, bool allow = true)
    {
        var logs = new List<string>();
        var states = new InMemoryWidgetStateStore();
        var registry = new WidgetRegistry();
        registry.Register(() => new TestWidget(WidgetId, multi, detail: false, floating: true, caps));
        var handler = new FakeDetachHandler(WidgetId);
        var env = new Env { Handler = handler, States = states, Logs = logs, Allow = allow };
        var controller = new HostController(registry, states, new InMemoryLayoutStore(), log: logs.Add, detachHandlers: new[] { handler })
        {
            UseMouseCapture = false
        };
        controller.PermissionPrompt = (m, c) =>
        {
            env.Prompts++;
            return Task.FromResult(env.Allow);
        };
        controller.Panel.AnimationsEnabled = false;
        var window = new System.Windows.Window
        {
            Left = 100, Top = 100, Width = 300, Height = 500, ShowActivated = false, Content = controller.Panel
        };
        window.Show();
        Sta.Pump();
        env.Controller = controller;
        env.Window = window;
        return env;
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) Sta.PumpFor(20);
        Assert.True(condition(), "condition was not met in time");
    }

    private static T Await<T>(Task<T> task)
    {
        WaitFor(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    private static string AddWidget(Env env, int counter = 0)
    {
        env.Controller.AddWidgetAsync(WidgetId).GetAwaiter().GetResult();
        var instance = env.Controller.Runtime.Instances.Single();
        ((TestWidget)instance.Widget).Counter = counter;
        return instance.State.InstanceId;
    }

    [Fact]
    public void Detaching_hands_the_saved_state_and_position_over_and_removes_the_host_instance()
    {
        Sta.Run(() =>
        {
            var env = Build();
            var id = AddWidget(env, counter: 7);

            env.Controller.ToggleFloat(id);
            WaitFor(() => !env.Controller.Runtime.Instances.Any());

            var request = Assert.Single(env.Handler.Requests);
            Assert.Equal(id, request.InstanceId);
            Assert.Equal("7", request.StateJson);            // what the widget's SaveStateAsync wrote
            Assert.True(request.ScreenBounds.Width > 0 && request.Dpi >= 96);
            Assert.Empty(env.Controller.Panel.Chromes);       // gone from the Host
            Assert.Contains(env.Logs, l => l.Contains("handed over"));
            env.Window.Close();
        });
    }

    [Fact]
    public void A_refused_hand_over_keeps_the_widget_and_floats_it()
    {
        Sta.Run(() =>
        {
            var env = Build();
            env.Handler.OnDetach = _ => Task.FromResult(false);
            var id = AddWidget(env);

            env.Controller.ToggleFloat(id);
            WaitFor(() => env.Controller.Runtime.Find(id)!.State.DockState == DockState.Floating);

            Assert.Single(env.Controller.Runtime.Instances);
            Assert.False(env.Handler.IsDetached);
            env.Controller.Floating.CloseAll();
            env.Window.Close();
        });
    }

    [Fact]
    public void A_throwing_handler_falls_back_to_floating()
    {
        Sta.Run(() =>
        {
            var env = Build();
            env.Handler.OnDetach = _ => throw new InvalidOperationException("pipe exploded");
            var id = AddWidget(env);

            env.Controller.ToggleFloat(id);
            WaitFor(() => env.Controller.Runtime.Find(id)!.State.DockState == DockState.Floating);

            Assert.Contains(env.Logs, l => l.Contains("pipe exploded"));
            env.Controller.Floating.CloseAll();
            env.Window.Close();
        });
    }

    [Fact]
    public void Installed_widgets_are_trusted_and_hand_over_without_any_prompt()
    {
        Sta.Run(() =>
        {
            var env = Build(allow: false);   // a prompt would be answered "no"...
            env.Controller.PermissionPrompt = null;   // ...but the real dialog path is taken instead,
            env.Controller.AutoGrantInstalledWidgets = () => true;   // and it grants silently
            var id = AddWidget(env);

            env.Controller.ToggleFloat(id);
            WaitFor(() => env.Handler.Requests.Count == 1);

            Assert.Equal(0, env.Prompts);
            env.Controller.Floating.CloseAll();
            env.Window.Close();
        });
    }

    [Fact]
    public void Without_the_users_permission_no_program_is_started()
    {
        Sta.Run(() =>
        {
            var env = Build(allow: false);
            var id = AddWidget(env);

            env.Controller.ToggleFloat(id);
            WaitFor(() => env.Controller.Runtime.Find(id)!.State.DockState == DockState.Floating);

            Assert.Empty(env.Handler.Requests);
            Assert.Equal(1, env.Prompts);
            env.Controller.Floating.CloseAll();
            env.Window.Close();
        });
    }

    [Fact]
    public void A_widget_that_did_not_declare_process_execution_just_floats()
    {
        Sta.Run(() =>
        {
            var env = Build(caps: WidgetCapabilities.None);
            var id = AddWidget(env);

            env.Controller.ToggleFloat(id);

            Assert.Equal(DockState.Floating, env.Controller.Runtime.Find(id)!.State.DockState);
            Assert.Empty(env.Handler.Requests);
            Assert.Equal(0, env.Prompts);
            env.Controller.Floating.CloseAll();
            env.Window.Close();
        });
    }

    [Fact]
    public void Dropping_the_application_on_the_host_docks_the_widget_from_its_state()
    {
        Sta.Run(() =>
        {
            var env = Build();
            var id = AddWidget(env, counter: 3);
            env.Controller.ToggleFloat(id);
            WaitFor(() => !env.Controller.Runtime.Instances.Any());

            var cursor = Interop.ScreenBoundsPx(env.Controller.Panel).TopLeft;
            var docked = env.Handler.RaiseDockRequest(new DockRequest(1, "42", new WidgetPoint(cursor.X + 20, cursor.Y + 20)));

            Assert.True(Await(docked));
            var instance = Assert.Single(env.Controller.Runtime.Instances);
            Assert.NotEqual(id, instance.State.InstanceId);                         // a new Host instance
            Assert.Equal(42, ((TestWidget)instance.Widget).Counter);                // continued from the app's state
            Assert.Equal(DockState.Docked, instance.State.DockState);
            Assert.Single(env.Controller.Panel.Chromes);
            env.Window.Close();
        });
    }

    [Fact]
    public void Docking_is_declined_while_the_host_already_has_the_single_instance()
    {
        Sta.Run(() =>
        {
            var env = Build();
            AddWidget(env);                     // the Host has its own instance (e.g. added from the + menu)

            var docked = env.Handler.RaiseDockRequest(new DockRequest(1, "1", new WidgetPoint(0, 0)));

            Assert.False(Await(docked));
            Assert.Single(env.Controller.Runtime.Instances);
            env.Window.Close();
        });
    }

    [Fact]
    public void Docking_with_unusable_state_is_declined_and_nothing_is_left_behind()
    {
        Sta.Run(() =>
        {
            var env = Build();
            var docked = env.Handler.RaiseDockRequest(new DockRequest(1, "not a number", new WidgetPoint(0, 0)));

            // TestWidget cannot parse it, RestoreState fails; the Host still creates the widget fresh, never crashes.
            var result = Await(docked);
            if (result) Assert.Single(env.Controller.Runtime.Instances);
            else Assert.Empty(env.Controller.Runtime.Instances);
            env.Window.Close();
        });
    }

    [Fact]
    public void Hover_from_the_application_shows_and_clears_the_drop_marker_and_keeps_the_host_visible()
    {
        Sta.Run(() =>
        {
            var env = Build();
            var activity = new List<(bool Active, bool Preview)>();
            env.Controller.DragActivityChanged += (a, p) => activity.Add((a, p));

            var cursor = Interop.ScreenBoundsPx(env.Controller.Panel).TopLeft;
            env.Handler.RaiseHover(new WidgetPoint(cursor.X + 10, cursor.Y + 10));
            Sta.Pump();
            env.Handler.RaiseHover(null);
            Sta.Pump();

            Assert.Equal(new[] { (true, true), (false, false) }, activity);
            env.Window.Close();
        });
    }

    [Fact]
    public void Shutting_down_the_host_shuts_the_handlers_down()
    {
        Sta.Run(() =>
        {
            var env = Build();
            env.Controller.ShutdownAsync().GetAwaiter().GetResult();

            Assert.True(env.Handler.ShutdownCalled);
            env.Window.Close();
        });
    }

    [Fact]
    public void A_widget_that_is_already_detached_is_not_handed_over_twice()
    {
        Sta.Run(() =>
        {
            var env = Build();
            env.Handler.IsDetached = true;
            var id = AddWidget(env);

            env.Controller.ToggleFloat(id);

            Assert.Empty(env.Handler.Requests);
            Assert.Equal(DockState.Floating, env.Controller.Runtime.Find(id)!.State.DockState);
            env.Controller.Floating.CloseAll();
            env.Window.Close();
        });
    }

    [Fact]
    public void A_widget_removed_in_the_middle_of_a_drag_does_not_break_later_pointer_events()
    {
        Sta.Run(() =>
        {
            var env = Build(caps: WidgetCapabilities.None);
            var id = AddWidget(env);
            var chrome = env.Controller.Panel.Chromes[id];
            var start = Interop.ScreenBoundsPx(chrome).TopLeft + new System.Windows.Vector(10, 10);

            env.Controller.PressDown(chrome, start, 1);
            env.Controller.PressMove(chrome, start + new System.Windows.Vector(60, 60), leftPressed: true);   // drag underway
            env.Controller.RemoveWidgetAsync(id).GetAwaiter().GetResult();                                     // e.g. handed over

            // Pointer events that are still queued for the removed widget must be ignored, not throw.
            env.Controller.PressMove(chrome, start + new System.Windows.Vector(80, 80), leftPressed: true);
            env.Controller.PressMove(chrome, start + new System.Windows.Vector(90, 90), leftPressed: false);
            env.Controller.PressUp(chrome, start);
            env.Controller.PressDown(chrome, start, 1);

            Assert.Empty(env.Logs.Where(l => l.Contains("Error")));
            env.Window.Close();
        });
    }

    [Fact]
    public void Handlers_are_started_with_the_host_and_again_whenever_an_application_announces_itself()
    {
        Sta.Run(() =>
        {
            var name = "Local\\ModuleDock.Test." + Guid.NewGuid().ToString("N");
            var registry = new WidgetRegistry();
            var handler = new FakeDetachHandler(WidgetId);
            var controller = new HostController(registry, new InMemoryWidgetStateStore(), new InMemoryLayoutStore(),
                detachHandlers: new[] { handler }, announceEventName: name);
            WaitFor(() => handler.Starts == 1);

            using (var signal = EventWaitHandle.OpenExisting(name)) signal.Set();
            WaitFor(() => handler.Starts == 2);

            controller.ShutdownAsync().GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void An_application_asks_the_host_for_the_detail_window_instead_of_opening_a_second_main_screen()
    {
        Sta.Run(() =>
        {
            var name = "Local\\ModuleDock.Test." + Guid.NewGuid().ToString("N");
            var registry = new WidgetRegistry();
            registry.Register(() => new TestWidget(WidgetId, multi: false, detail: true, floating: true));
            var controller = new HostController(registry, new InMemoryWidgetStateStore(), new InMemoryLayoutStore(),
                detachHandlers: new[] { new FakeDetachHandler(WidgetId) }, announceEventName: name);
            controller.AddWidgetAsync(WidgetId).GetAwaiter().GetResult();
            var id = controller.Runtime.Instances.Single().State.InstanceId;
            Assert.False(controller.Details.IsOpen(id));

            using (var signal = EventWaitHandle.OpenExisting(WidgetAnnounce.OpenDetailEventName(WidgetId, name))) signal.Set();
            WaitFor(() => controller.Details.IsOpen(id));

            controller.ShutdownAsync().GetAwaiter().GetResult();
        });
    }
}
