using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

public sealed class WidgetEventBus : IWidgetEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Action<object?>>> _handlers = new();

    public void Publish(string topic, object? payload = null)
    {
        Action<object?>[] snapshot;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(topic, out var list)) return;
            snapshot = list.ToArray();
        }
        foreach (var h in snapshot)
        {
            try { h(payload); }
            catch { /* a faulty subscriber must not break other widgets */ }
        }
    }

    public IDisposable Subscribe(string topic, Action<object?> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(topic, out var list))
                _handlers[topic] = list = new();
            list.Add(handler);
        }
        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(topic, out var list)) list.Remove(handler);
            }
        });
    }
}

internal sealed class Subscription : IDisposable
{
    private Action? _dispose;
    public Subscription(Action dispose) => _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

public sealed class WidgetCommandBus : IWidgetCommandBus
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<object?, CancellationToken, Task>> _handlers = new();

    public Task ExecuteAsync(string command, object? argument = null, CancellationToken cancellationToken = default)
    {
        Func<object?, CancellationToken, Task>? h;
        lock (_gate) _handlers.TryGetValue(command, out h);
        if (h is null) throw new InvalidOperationException($"No handler registered for command '{command}'.");
        return h(argument, cancellationToken);
    }

    public IDisposable Register(string command, Func<object?, CancellationToken, Task> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryAdd(command, handler))
                throw new InvalidOperationException($"Command '{command}' is already registered.");
        }
        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(command, out var cur) && cur == handler) _handlers.Remove(command);
            }
        });
    }
}

/// <summary>Per-instance settings bucket.</summary>
public sealed class WidgetSettings : IWidgetSettings
{
    private readonly Dictionary<string, string?> _values = new();
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string? value) => _values[key] = value;
}

public sealed class WidgetPermissionService : IWidgetPermissionService
{
    private readonly WidgetCapabilities _declared;
    private WidgetCapabilities _granted;
    private readonly Func<WidgetCapabilities, Task<bool>>? _prompt;
    private readonly Action<WidgetCapabilities>? _onGranted;

    /// <param name="declared">Capabilities the manifest declared; undeclared ones are never granted.</param>
    /// <param name="granted">Capabilities already granted by the Host/user.</param>
    /// <param name="prompt">Optional user prompt used by <see cref="RequestAsync"/>.</param>
    /// <param name="onGranted">Called after a prompt grants a capability (used to persist the decision).</param>
    public WidgetPermissionService(WidgetCapabilities declared, WidgetCapabilities granted,
        Func<WidgetCapabilities, Task<bool>>? prompt = null, Action<WidgetCapabilities>? onGranted = null)
    {
        _declared = declared;
        _granted = granted & declared;
        _prompt = prompt;
        _onGranted = onGranted;
    }

    public bool IsGranted(WidgetCapabilities capability) =>
        capability != WidgetCapabilities.None && (_granted & capability) == capability;

    public async Task<bool> RequestAsync(WidgetCapabilities capability, CancellationToken cancellationToken = default)
    {
        if (capability == WidgetCapabilities.None) return false;
        if ((_declared & capability) != capability) return false; // undeclared use is refused
        if (IsGranted(capability)) return true;
        if (_prompt is null) return false;
        if (!await _prompt(capability)) return false;
        _granted |= capability;
        _onGranted?.Invoke(capability);
        return true;
    }
}

public sealed class NullNotificationService : IWidgetNotificationService
{
    public void Notify(string title, string message) { }
}

public sealed class DelegateNotificationService : IWidgetNotificationService
{
    private readonly Action<string, string> _notify;
    public DelegateNotificationService(Action<string, string> notify) => _notify = notify;
    public void Notify(string title, string message) => _notify(title, message);
}

public sealed class InMemoryLogger : IWidgetLogger
{
    private readonly string _prefix;
    private readonly List<string> _lines;
    public InMemoryLogger(string prefix, List<string>? sink = null)
    {
        _prefix = prefix;
        _lines = sink ?? new();
    }
    public IReadOnlyList<string> Lines => _lines;
    public void Log(string level, string message, Exception? exception = null)
    {
        lock (_lines) _lines.Add($"[{level}] {_prefix}: {message}{(exception is null ? "" : " " + exception.Message)}");
    }
}

public sealed class WidgetContext : IWidgetContext
{
    public required string InstanceId { get; init; }
    public required IWidgetEventBus Events { get; init; }
    public required IWidgetCommandBus Commands { get; init; }
    public required IWidgetSettings Settings { get; init; }
    public required IWidgetNotificationService Notifications { get; init; }
    public required IWidgetPermissionService Permissions { get; init; }
    public required IWidgetLogger Logger { get; init; }
}
