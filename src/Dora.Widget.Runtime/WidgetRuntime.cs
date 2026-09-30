using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

public sealed class WidgetRegistrationException : Exception
{
    public WidgetRegistrationException(string message) : base(message) { }
}

/// <summary>Catalog of widget types available to the Host.</summary>
public sealed class WidgetRegistry
{
    private readonly Dictionary<string, Func<IComposableWidget>> _factories = new();
    private readonly Dictionary<string, WidgetManifest> _manifests = new();
    private readonly Version _hostContract;

    public WidgetRegistry(Version? hostContract = null) => _hostContract = hostContract ?? ContractInfo.Current;

    public IReadOnlyCollection<WidgetManifest> Manifests => _manifests.Values;

    /// <summary>Validates and registers a widget type. Throws when the manifest is invalid.</summary>
    public IReadOnlyList<ValidationIssue> Register(Func<IComposableWidget> factory)
    {
        var probe = factory();
        var manifest = probe.Manifest;
        var issues = ManifestValidator.Validate(manifest, _hostContract);
        if (!ManifestValidator.IsValid(issues))
            throw new WidgetRegistrationException(
                $"Widget '{manifest.Id}' rejected: " +
                string.Join("; ", issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.Message)));
        if (_manifests.ContainsKey(manifest.Id))
            throw new WidgetRegistrationException($"Widget id '{manifest.Id}' is already registered.");

        _manifests[manifest.Id] = manifest;
        _factories[manifest.Id] = factory;
        return issues;
    }

    public bool TryGetManifest(string widgetId, out WidgetManifest manifest) =>
        _manifests.TryGetValue(widgetId, out manifest!);

    internal IComposableWidget Create(string widgetId) =>
        _factories.TryGetValue(widgetId, out var f)
            ? f()
            : throw new WidgetRegistrationException($"Unknown widget '{widgetId}'.");
}

/// <summary>A live widget instance owned by the Host.</summary>
public sealed class WidgetInstance
{
    public required WidgetRuntimeState State { get; init; }
    public required IComposableWidget Widget { get; init; }
    public required IWidgetContext Context { get; init; }
    public WidgetManifest Manifest => Widget.Manifest;

    /// <summary>Created lazily; the same feature ViewModel backs every surface.</summary>
    public object? SummaryView { get; internal set; }
}

/// <summary>Creates, restores, persists and tears down widget instances.</summary>
public sealed class WidgetRuntime
{
    private readonly WidgetRegistry _registry;
    private readonly WidgetStateMachine _machine;
    private readonly IWidgetStateStore _stateStore;
    private readonly IWidgetEventBus _events;
    private readonly IWidgetCommandBus _commands;
    private readonly IWidgetNotificationService _notifications;
    private readonly Func<WidgetManifest, IWidgetPermissionService> _permissions;
    private readonly Action<string, string, Exception?>? _log;
    private readonly Dictionary<string, WidgetInstance> _instances = new();

    public WidgetRuntime(
        WidgetRegistry registry,
        WidgetStateMachine machine,
        IWidgetStateStore stateStore,
        IWidgetEventBus? events = null,
        IWidgetCommandBus? commands = null,
        IWidgetNotificationService? notifications = null,
        Func<WidgetManifest, IWidgetPermissionService>? permissions = null,
        Action<string, string, Exception?>? log = null)
    {
        _registry = registry;
        _machine = machine;
        _stateStore = stateStore;
        _events = events ?? new WidgetEventBus();
        _commands = commands ?? new WidgetCommandBus();
        _notifications = notifications ?? new NullNotificationService();
        _permissions = permissions ?? (m => new WidgetPermissionService(m.Capabilities, WidgetCapabilities.None));
        _log = log;
    }

    /// <summary>Shared event bus; the Host listens on it for Host-level topics such as <c>host.attention</c>.</summary>
    public IWidgetEventBus Events => _events;

    public IReadOnlyCollection<WidgetInstance> Instances => _instances.Values;

    public WidgetInstance? Find(string instanceId) => _instances.GetValueOrDefault(instanceId);

    public async Task<WidgetInstance> CreateInstanceAsync(
        string widgetId, string? instanceId = null, CancellationToken ct = default)
    {
        if (!_registry.TryGetManifest(widgetId, out var manifest))
            throw new WidgetRegistrationException($"Unknown widget '{widgetId}'.");

        if (!manifest.AllowMultipleInstances && _instances.Values.Any(i => i.State.WidgetId == widgetId))
            throw new InvalidOperationException($"Widget '{widgetId}' does not allow multiple instances.");

        instanceId ??= Guid.NewGuid().ToString("D");
        if (_instances.ContainsKey(instanceId))
            throw new InvalidOperationException($"Instance '{instanceId}' already exists.");

        var widget = _registry.Create(widgetId);
        var state = new WidgetRuntimeState { InstanceId = instanceId, WidgetId = widgetId };
        var context = new WidgetContext
        {
            InstanceId = instanceId,
            Events = _events,
            Commands = _commands,
            Settings = new WidgetSettings(),
            Notifications = _notifications,
            Permissions = _permissions(manifest),
            Logger = new HostLogger($"{manifest.Id}/{instanceId}", _log)
        };

        await widget.InitializeAsync(context, ct);
        try
        {
            await widget.RestoreStateAsync(new StateReader(_stateStore.Load(instanceId)));
        }
        catch (Exception ex)
        {
            // A widget that cannot restore its state starts fresh; it must not take the Host down.
            _log?.Invoke("Warn", $"{manifest.Id}: state restore failed", ex);
        }

        var instance = new WidgetInstance { State = state, Widget = widget, Context = context };
        _instances[instanceId] = instance;
        _machine.Register(state);
        return instance;
    }

    public object GetSummaryView(WidgetInstance instance) =>
        instance.SummaryView ??= instance.Widget.CreateSummaryView(instance.Context);

    /// <summary>Returns a detail view, or null when the widget does not support one.</summary>
    public object? CreateDetailView(WidgetInstance instance)
    {
        if (!instance.Manifest.SupportsDetailView) return null;
        var view = instance.Widget.CreateDetailView(instance.Context);
        if (view is null)
            _log?.Invoke("Error", $"{instance.Manifest.Id}: SupportsDetailView=true but CreateDetailView returned null", null);
        return view;
    }

    public async Task SaveStateAsync(WidgetInstance instance)
    {
        var writer = new StateWriter();
        await instance.Widget.SaveStateAsync(writer);
        if (writer.Result is not null) _stateStore.Save(instance.State.InstanceId, writer.Result);
    }

    public async Task SaveAllAsync()
    {
        foreach (var i in _instances.Values.ToList())
        {
            try { await SaveStateAsync(i); }
            catch (Exception ex) { _log?.Invoke("Error", $"{i.Manifest.Id}: save failed", ex); }
        }
    }

    /// <param name="deleteState">True when the user removes the instance for good.</param>
    public async Task RemoveInstanceAsync(string instanceId, bool deleteState, CancellationToken ct = default)
    {
        if (!_instances.Remove(instanceId, out var instance)) return;
        _machine.Unregister(instanceId);
        try
        {
            if (deleteState) _stateStore.Delete(instanceId);
            else await SaveStateAsync(instance);
        }
        finally
        {
            try { await instance.Widget.ShutdownAsync(ct); }
            catch (Exception ex) { _log?.Invoke("Error", $"{instance.Manifest.Id}: shutdown failed", ex); }
        }
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        foreach (var id in _instances.Keys.ToList())
            await RemoveInstanceAsync(id, deleteState: false, ct);
    }

    private sealed class HostLogger : IWidgetLogger
    {
        private readonly string _name;
        private readonly Action<string, string, Exception?>? _sink;
        public HostLogger(string name, Action<string, string, Exception?>? sink) { _name = name; _sink = sink; }
        public void Log(string level, string message, Exception? exception = null) =>
            _sink?.Invoke(level, $"{_name}: {message}", exception);
    }
}
