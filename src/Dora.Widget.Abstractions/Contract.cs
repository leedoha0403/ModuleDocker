namespace Dora.Widget.Abstractions;

public enum DockState { Docked, Floating }

public enum WidgetDisplayMode { Collapsed, Compact, Natural }

public enum WidgetInteractionState
{
    Idle,
    HoverPending,
    Focused,
    FocusReleasePending,
    Pinned,
    Dragging
}

/// <summary>UI-framework neutral size (DIP).</summary>
public readonly record struct WidgetSize(double Width, double Height)
{
    public bool FitsIn(WidgetSize available) => Width <= available.Width && Height <= available.Height;
}

/// <summary>UI-framework neutral rectangle (DIP).</summary>
public readonly record struct WidgetRect(double X, double Y, double Width, double Height);

public sealed record WidgetLayoutProfile
{
    public required WidgetSize NaturalSize { get; init; }
    public required WidgetSize CompactSize { get; init; }
    public required WidgetSize CollapsedSize { get; init; }

    public required WidgetSize MinNaturalSize { get; init; }
    public required WidgetSize MinCompactSize { get; init; }
    public required WidgetSize MinCollapsedSize { get; init; }

    public WidgetSize PreferredSize(WidgetDisplayMode mode) => mode switch
    {
        WidgetDisplayMode.Natural => NaturalSize,
        WidgetDisplayMode.Compact => CompactSize,
        _ => CollapsedSize
    };

    public WidgetSize MinimumSize(WidgetDisplayMode mode) => mode switch
    {
        WidgetDisplayMode.Natural => MinNaturalSize,
        WidgetDisplayMode.Compact => MinCompactSize,
        _ => MinCollapsedSize
    };
}

[Flags]
public enum WidgetCapabilities
{
    None = 0,
    FileSystem = 1 << 0,
    Network = 1 << 1,
    ProcessExecution = 1 << 2,
    Clipboard = 1 << 3,
    Notifications = 1 << 4,
    Git = 1 << 5,
    Shell = 1 << 6
}

public sealed record WidgetManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Version Version { get; init; }
    public required Version ContractVersion { get; init; }

    public required WidgetLayoutProfile Layout { get; init; }
    public required WidgetCapabilities Capabilities { get; init; }

    public required bool AllowMultipleInstances { get; init; }
    public required bool SupportsDetailView { get; init; }
    public required bool SupportsFloating { get; init; }

    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? IconKey { get; init; }
    public string? Category { get; init; }
}

public static class ContractInfo
{
    /// <summary>Contract version implemented by this Host.</summary>
    public static readonly Version Current = new(1, 0, 0);
}

public interface IWidgetStateWriter
{
    /// <summary>Writes feature state with a schema version.</summary>
    void Write(int stateVersion, string json);
}

public interface IWidgetStateReader
{
    /// <summary>Returns false when no state has been saved.</summary>
    bool TryRead(out int stateVersion, out string json);
}

public interface IWidgetEventBus
{
    void Publish(string topic, object? payload = null);
    IDisposable Subscribe(string topic, Action<object?> handler);
}

public interface IWidgetCommandBus
{
    Task ExecuteAsync(string command, object? argument = null, CancellationToken cancellationToken = default);
    IDisposable Register(string command, Func<object?, CancellationToken, Task> handler);
}

public interface IWidgetSettings
{
    string? Get(string key);
    void Set(string key, string? value);
}

public interface IWidgetNotificationService
{
    void Notify(string title, string message);
}

public interface IWidgetPermissionService
{
    bool IsGranted(WidgetCapabilities capability);
    Task<bool> RequestAsync(WidgetCapabilities capability, CancellationToken cancellationToken = default);
}

public interface IWidgetLogger
{
    void Log(string level, string message, Exception? exception = null);
}

public interface IWidgetContext
{
    string InstanceId { get; }
    IWidgetEventBus Events { get; }
    IWidgetCommandBus Commands { get; }
    IWidgetSettings Settings { get; }
    IWidgetNotificationService Notifications { get; }
    IWidgetPermissionService Permissions { get; }
    IWidgetLogger Logger { get; }
}

public interface IComposableWidget
{
    WidgetManifest Manifest { get; }

    Task InitializeAsync(IWidgetContext context, CancellationToken cancellationToken);

    /// <summary>Returns UI supporting Natural/Compact/Collapsed; the Host picks the mode.</summary>
    object CreateSummaryView(IWidgetContext context);

    object? CreateDetailView(IWidgetContext context);

    Task SaveStateAsync(IWidgetStateWriter writer);

    Task RestoreStateAsync(IWidgetStateReader reader);

    Task ShutdownAsync(CancellationToken cancellationToken);
}

/// <summary>Optional: lets a summary view learn the Host-selected display mode.</summary>
public interface IDisplayModeAware
{
    void OnDisplayModeChanged(WidgetDisplayMode mode);
}
