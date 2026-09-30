using System.Text.Json;
using DiskUsage.Core;
using DiskUsage.Presentation;
using Dora.Widget.Abstractions;

namespace DiskUsage.Widget;

/// <summary>Blocks disk reads until the Host has granted the FileSystem capability.</summary>
internal sealed class GatedDiskService : IDiskService
{
    private readonly IDiskService _inner;
    public GatedDiskService(IDiskService inner) => _inner = inner;
    public bool Allowed { get; set; }

    public IReadOnlyList<DriveReading> Read() =>
        Allowed ? _inner.Read() : throw new UnauthorizedAccessException("disk access was not granted");
}

public sealed class DiskWidget : IComposableWidget
{
    private const int StateVersion = 1;

    // Host-level topic (see the Host's edge snap / auto-hide feature): lights the handle while the Host is hidden.
    private const string AttentionTopic = "host.attention";

    private readonly GatedDiskService _gate;
    private DiskViewModel? _vm;
    private IWidgetContext? _context;
    private IDisposable? _command;
    private string? _pendingSelection;

    /// <summary>The Host loads widgets through this parameterless constructor.</summary>
    public DiskWidget() : this(new SystemDiskService()) { }

    public DiskWidget(IDiskService disks) => _gate = new GatedDiskService(disks);

    public WidgetManifest Manifest { get; } = new()
    {
        Id = "dev.leedoha.diskusage.overview",
        Name = "Disk Usage",
        Description = "Used space of your drives.",
        Version = new Version(1, 0, 0),
        ContractVersion = new Version(1, 0, 0),
        Layout = new WidgetLayoutProfile
        {
            NaturalSize = new(260, 120),
            CompactSize = new(260, 40),
            CollapsedSize = new(260, 24),
            MinNaturalSize = new(160, 80),
            MinCompactSize = new(120, 40),
            MinCollapsedSize = new(60, 24)
        },
        Capabilities = WidgetCapabilities.FileSystem,
        AllowMultipleInstances = false,
        SupportsDetailView = true,
        SupportsFloating = true
    };

    private DiskViewModel Vm => _vm ?? throw new InvalidOperationException("Widget is not initialized.");

    public async Task InitializeAsync(IWidgetContext context, CancellationToken cancellationToken)
    {
        _context = context;
        // Declared capabilities still need the user's grant; ask through the Host and never bypass it.
        _gate.Allowed = context.Permissions.IsGranted(WidgetCapabilities.FileSystem) ||
                        await context.Permissions.RequestAsync(WidgetCapabilities.FileSystem, cancellationToken);

        _vm = new DiskViewModel(_gate, TimeSpan.FromSeconds(30));
        _vm.DriveSelected += path => context.Events.Publish("filesystem.path.selected", path);
        _vm.CriticalChanged += critical => context.Events.Publish(AttentionTopic, critical);
        _command = context.Commands.Register("diskusage.overview.refresh", (_, _) =>
        {
            Vm.Refresh();
            return Task.CompletedTask;
        });

        if (!_gate.Allowed) context.Logger.Log("Warn", "FileSystem permission not granted; showing no disks.");
        if (_vm.HasCritical) context.Events.Publish(AttentionTopic, true);
        if (_pendingSelection != null) _vm.SelectSilently(_pendingSelection);
    }

    public object CreateSummaryView(IWidgetContext context) => new DiskSummaryView(Vm);

    public object? CreateDetailView(IWidgetContext context) => new DiskDetailView(Vm);

    public Task SaveStateAsync(IWidgetStateWriter writer)
    {
        writer.Write(StateVersion, JsonSerializer.Serialize(new DiskState(_vm?.Selected?.Name)));
        return Task.CompletedTask;
    }

    public Task RestoreStateAsync(IWidgetStateReader reader)
    {
        if (!reader.TryRead(out var version, out var json) || version < 1) return Task.CompletedTask;
        _pendingSelection = JsonSerializer.Deserialize<DiskState>(json)?.SelectedDrive;
        _vm?.SelectSilently(_pendingSelection);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _command?.Dispose();
        _vm?.Dispose();
        _vm = null;
        return Task.CompletedTask;
    }

    private sealed record DiskState(string? SelectedDrive);
}
