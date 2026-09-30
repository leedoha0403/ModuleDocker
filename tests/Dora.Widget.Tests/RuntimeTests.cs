using Dora.Widget.Abstractions;
using Dora.Widget.Runtime;
using Xunit;

namespace Dora.Widget.Tests;

internal sealed class FakeWidget : IComposableWidget
{
    public FakeWidget(WidgetManifest manifest) => Manifest = manifest;
    public WidgetManifest Manifest { get; }
    public string Value = "";
    public bool ShutDown;
    public bool ThrowOnRestore;
    public bool DetailReturnsNull;

    public Task InitializeAsync(IWidgetContext context, CancellationToken ct) => Task.CompletedTask;
    public object CreateSummaryView(IWidgetContext context) => new object();
    public object? CreateDetailView(IWidgetContext context) => DetailReturnsNull ? null : new object();

    public Task SaveStateAsync(IWidgetStateWriter writer)
    {
        writer.Write(2, Value);
        return Task.CompletedTask;
    }

    public Task RestoreStateAsync(IWidgetStateReader reader)
    {
        if (ThrowOnRestore) throw new InvalidOperationException("boom");
        if (reader.TryRead(out _, out var json)) Value = json;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        ShutDown = true;
        return Task.CompletedTask;
    }
}

public class ValidatorTests
{
    [Fact]
    public void Valid_manifest_passes()
    {
        Assert.True(ManifestValidator.IsValid(ManifestValidator.Validate(T.Manifest())));
    }

    [Fact]
    public void Major_contract_mismatch_is_error()
    {
        var m = T.Manifest() with { ContractVersion = new Version(2, 0, 0) };
        Assert.False(ManifestValidator.IsValid(ManifestValidator.Validate(m)));
    }

    [Fact]
    public void Newer_minor_contract_is_warning_only()
    {
        var m = T.Manifest() with { ContractVersion = new Version(1, 5, 0) };
        var issues = ManifestValidator.Validate(m);
        Assert.True(ManifestValidator.IsValid(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void Min_size_larger_than_preferred_is_error()
    {
        var profile = T.Profile() with { MinCompactSize = new WidgetSize(500, 40) };
        var m = T.Manifest() with { Layout = profile };
        Assert.False(ManifestValidator.IsValid(ManifestValidator.Validate(m)));
    }

    [Fact]
    public void Display_name_style_id_warns()
    {
        var issues = ManifestValidator.Validate(T.Manifest("My Widget"));
        Assert.Contains(issues, i => i.Message.Contains("reverse-domain"));
    }

    [Fact]
    public void Blank_id_is_error()
    {
        Assert.False(ManifestValidator.IsValid(ManifestValidator.Validate(T.Manifest(" "))));
    }
}

public class RuntimeTests
{
    private static (WidgetRuntime rt, WidgetRegistry reg, InMemoryWidgetStateStore store, List<FakeWidget> made)
        Make(WidgetManifest? manifest = null)
    {
        var reg = new WidgetRegistry();
        var made = new List<FakeWidget>();
        manifest ??= T.Manifest();
        reg.Register(() => { var w = new FakeWidget(manifest); made.Add(w); return w; });
        var store = new InMemoryWidgetStateStore();
        var rt = new WidgetRuntime(reg, new WidgetStateMachine(new ManualTimerScheduler()), store);
        return (rt, reg, store, made);
    }

    [Fact]
    public void Registry_rejects_invalid_and_duplicate()
    {
        var reg = new WidgetRegistry();
        Assert.Throws<WidgetRegistrationException>(() =>
            reg.Register(() => new FakeWidget(T.Manifest() with { ContractVersion = new Version(9, 0) })));
        reg.Register(() => new FakeWidget(T.Manifest()));
        Assert.Throws<WidgetRegistrationException>(() => reg.Register(() => new FakeWidget(T.Manifest())));
    }

    [Fact]
    public async Task Single_instance_widgets_reject_second_instance()
    {
        var (rt, _, _, _) = Make();
        await rt.CreateInstanceAsync("dev.test.sample");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.CreateInstanceAsync("dev.test.sample"));
    }

    [Fact]
    public async Task Multi_instance_widgets_get_distinct_instance_ids()
    {
        var (rt, _, _, _) = Make(T.Manifest(multi: true));
        var a = await rt.CreateInstanceAsync("dev.test.sample");
        var b = await rt.CreateInstanceAsync("dev.test.sample");
        Assert.NotEqual(a.State.InstanceId, b.State.InstanceId);
        Assert.NotEqual(a.State.InstanceId, a.State.WidgetId);
        Assert.Equal(a.State.InstanceId, a.Context.InstanceId);
    }

    [Fact]
    public async Task State_round_trips_through_store_on_remove_and_recreate()
    {
        var (rt, _, store, made) = Make();
        var inst = await rt.CreateInstanceAsync("dev.test.sample", "inst-1");
        ((FakeWidget)inst.Widget).Value = "hello";
        await rt.RemoveInstanceAsync("inst-1", deleteState: false);
        Assert.True(((FakeWidget)inst.Widget).ShutDown);
        Assert.Equal(2, store.Load("inst-1")!.StateVersion);

        var again = await rt.CreateInstanceAsync("dev.test.sample", "inst-1");
        Assert.Equal("hello", ((FakeWidget)again.Widget).Value);
    }

    [Fact]
    public async Task Delete_state_removes_stored_state()
    {
        var (rt, _, store, _) = Make();
        var inst = await rt.CreateInstanceAsync("dev.test.sample", "inst-1");
        ((FakeWidget)inst.Widget).Value = "x";
        await rt.SaveStateAsync(inst);
        await rt.RemoveInstanceAsync("inst-1", deleteState: true);
        Assert.Null(store.Load("inst-1"));
    }

    [Fact]
    public async Task Restore_failure_does_not_fail_creation()
    {
        var reg = new WidgetRegistry();
        reg.Register(() => new FakeWidget(T.Manifest()) { ThrowOnRestore = true });
        var logs = new List<string>();
        var rt = new WidgetRuntime(reg, new WidgetStateMachine(new ManualTimerScheduler()),
            new InMemoryWidgetStateStore(), log: (l, m, e) => logs.Add(l));
        var inst = await rt.CreateInstanceAsync("dev.test.sample");
        Assert.NotNull(inst);
        Assert.Contains("Warn", logs);
    }

    [Fact]
    public async Task Detail_view_null_when_unsupported()
    {
        var (rt, _, _, _) = Make(T.Manifest(detail: false));
        var inst = await rt.CreateInstanceAsync("dev.test.sample");
        Assert.Null(rt.CreateDetailView(inst));
    }

    [Fact]
    public async Task Summary_view_is_created_once_per_instance()
    {
        var (rt, _, _, _) = Make();
        var inst = await rt.CreateInstanceAsync("dev.test.sample");
        Assert.Same(rt.GetSummaryView(inst), rt.GetSummaryView(inst));
    }

    [Fact]
    public async Task Instance_is_registered_with_state_machine()
    {
        var reg = new WidgetRegistry();
        reg.Register(() => new FakeWidget(T.Manifest()));
        var sm = new WidgetStateMachine(new ManualTimerScheduler());
        var rt = new WidgetRuntime(reg, sm, new InMemoryWidgetStateStore());
        var inst = await rt.CreateInstanceAsync("dev.test.sample");
        sm.Click(inst.State.InstanceId);
        Assert.Equal(WidgetInteractionState.Focused, inst.State.InteractionState);
        await rt.ShutdownAsync();
        Assert.Empty(sm.States);
    }
}

public class ServiceTests
{
    [Fact]
    public async Task Permissions_never_grant_undeclared_capabilities()
    {
        var p = new WidgetPermissionService(WidgetCapabilities.Network, WidgetCapabilities.Network | WidgetCapabilities.Shell,
            _ => Task.FromResult(true));
        Assert.True(p.IsGranted(WidgetCapabilities.Network));
        Assert.False(p.IsGranted(WidgetCapabilities.Shell));
        Assert.False(await p.RequestAsync(WidgetCapabilities.Shell));
        Assert.False(p.IsGranted(WidgetCapabilities.Shell));
    }

    [Fact]
    public async Task Permission_prompt_grants_declared_capability()
    {
        var p = new WidgetPermissionService(WidgetCapabilities.Clipboard, WidgetCapabilities.None,
            _ => Task.FromResult(true));
        Assert.False(p.IsGranted(WidgetCapabilities.Clipboard));
        Assert.True(await p.RequestAsync(WidgetCapabilities.Clipboard));
        Assert.True(p.IsGranted(WidgetCapabilities.Clipboard));
    }

    [Fact]
    public async Task Permission_denied_without_prompt()
    {
        var p = new WidgetPermissionService(WidgetCapabilities.Clipboard, WidgetCapabilities.None);
        Assert.False(await p.RequestAsync(WidgetCapabilities.Clipboard));
        Assert.False(p.IsGranted(WidgetCapabilities.None));
    }

    [Fact]
    public void Event_bus_delivers_isolates_faults_and_unsubscribes()
    {
        var bus = new WidgetEventBus();
        var got = new List<object?>();
        bus.Subscribe("a.b.c", _ => throw new Exception("bad"));
        var sub = bus.Subscribe("a.b.c", p => got.Add(p));
        bus.Publish("a.b.c", 1);
        sub.Dispose();
        bus.Publish("a.b.c", 2);
        Assert.Equal(new object?[] { 1 }, got);
    }

    [Fact]
    public async Task Command_bus_executes_and_rejects_duplicates()
    {
        var bus = new WidgetCommandBus();
        var hit = 0;
        using var reg = bus.Register("refresh.usage", (_, _) => { hit++; return Task.CompletedTask; });
        await bus.ExecuteAsync("refresh.usage");
        Assert.Equal(1, hit);
        Assert.Throws<InvalidOperationException>(() => bus.Register("refresh.usage", (_, _) => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.ExecuteAsync("missing"));
    }
}

public class PersistenceTests
{
    [Fact]
    public void Layout_captures_only_host_state_and_orders_docked_first()
    {
        var lm = new LayoutManager(new InMemoryLayoutStore());
        var pinned = new WidgetRuntimeState { InstanceId = "a", WidgetId = "w", DockOrder = 1,
            InteractionState = WidgetInteractionState.Pinned };
        var dragging = new WidgetRuntimeState { InstanceId = "b", WidgetId = "w", DockOrder = 0,
            InteractionState = WidgetInteractionState.Dragging,
            PreviousInteractionState = WidgetInteractionState.Pinned };
        var floating = new WidgetRuntimeState { InstanceId = "c", WidgetId = "w", DockState = DockState.Floating,
            FloatingBounds = new WidgetRect(1, 2, 3, 4) };
        var snap = lm.Capture(new[] { floating, pinned, dragging }, new WidgetRect(0, 0, 10, 10));
        Assert.Equal(new[] { "b", "a", "c" }, snap.Entries.Select(e => e.InstanceId));
        Assert.True(snap.Entries[0].Pinned);
        Assert.True(snap.Entries[1].Pinned);
        Assert.False(snap.Entries[2].Pinned);
    }

    [Fact]
    public void Json_layout_store_round_trips_and_survives_corruption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "moduledock-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "layout.json");
            var store = new JsonLayoutStore(path);
            Assert.Null(store.Load());

            var snap = new LayoutSnapshot(LayoutSnapshot.CurrentSchema, new WidgetRect(1, 2, 3, 4), new[]
            {
                new LayoutEntry("i1", "w", DockState.Floating, 0, true, new WidgetRect(5, 6, 7, 8))
            });
            store.Save(snap);
            var loaded = store.Load()!;
            Assert.Equal(snap.HostBounds, loaded.HostBounds);
            Assert.Equal(DockState.Floating, loaded.Entries[0].DockState);
            Assert.Equal(new WidgetRect(5, 6, 7, 8), loaded.Entries[0].FloatingBounds);
            Assert.Contains("Floating", File.ReadAllText(path));

            File.WriteAllText(path, "{ not json");
            Assert.Null(store.Load());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void File_state_store_round_trips_and_rejects_path_ids()
    {
        var dir = Path.Combine(Path.GetTempPath(), "moduledock-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileWidgetStateStore(dir);
            store.Save("abc", new StoredWidgetState(3, "{\"x\":1}"));
            Assert.Equal(new StoredWidgetState(3, "{\"x\":1}"), store.Load("abc"));
            store.Delete("abc");
            Assert.Null(store.Load("abc"));
            Assert.Throws<ArgumentException>(() => store.Load("..\\evil"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

public class PermissionGrantTests
{
    [Fact]
    public async Task Prompt_grant_is_reported_so_it_can_be_persisted()
    {
        var store = new InMemoryPermissionGrantStore();
        var p = new WidgetPermissionService(WidgetCapabilities.FileSystem | WidgetCapabilities.Network,
            store.Get("w"), _ => Task.FromResult(true), g => store.Set("w", store.Get("w") | g));
        Assert.True(await p.RequestAsync(WidgetCapabilities.FileSystem));
        Assert.Equal(WidgetCapabilities.FileSystem, store.Get("w"));

        // a fresh service for the next session starts with the stored grant and does not prompt again
        var prompts = 0;
        var again = new WidgetPermissionService(WidgetCapabilities.FileSystem | WidgetCapabilities.Network,
            store.Get("w"), _ => { prompts++; return Task.FromResult(false); });
        Assert.True(again.IsGranted(WidgetCapabilities.FileSystem));
        Assert.True(await again.RequestAsync(WidgetCapabilities.FileSystem));
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task Denied_prompt_is_not_stored()
    {
        var store = new InMemoryPermissionGrantStore();
        var p = new WidgetPermissionService(WidgetCapabilities.Clipboard, WidgetCapabilities.None,
            _ => Task.FromResult(false), g => store.Set("w", g));
        Assert.False(await p.RequestAsync(WidgetCapabilities.Clipboard));
        Assert.Equal(WidgetCapabilities.None, store.Get("w"));
    }

    [Fact]
    public void Json_store_round_trips_ignores_unknown_names_and_survives_corruption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "md-perm-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "permissions.json");
        try
        {
            var store = new JsonPermissionGrantStore(path);
            Assert.Equal(WidgetCapabilities.None, store.Get("a"));
            store.Set("a", WidgetCapabilities.FileSystem | WidgetCapabilities.Git);
            store.Set("b", WidgetCapabilities.Network);

            var reloaded = new JsonPermissionGrantStore(path);
            Assert.Equal(WidgetCapabilities.FileSystem | WidgetCapabilities.Git, reloaded.Get("a"));
            Assert.Equal(WidgetCapabilities.Network, reloaded.Get("b"));
            Assert.Contains("\"FileSystem\"", File.ReadAllText(path));

            File.WriteAllText(path, "{\"grants\":{\"a\":[\"FileSystem\",\"Bogus\"]}}");
            Assert.Equal(WidgetCapabilities.FileSystem, new JsonPermissionGrantStore(path).Get("a"));

            File.WriteAllText(path, "not json");
            Assert.Equal(WidgetCapabilities.None, new JsonPermissionGrantStore(path).Get("a"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
