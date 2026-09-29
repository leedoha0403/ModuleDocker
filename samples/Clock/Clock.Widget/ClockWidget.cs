using System.Text.Json;
using Clock.Core;
using Clock.Presentation;
using Dora.Widget.Abstractions;

namespace Clock.Widget;

public sealed class ClockWidget : IComposableWidget
{
    private const int StateVersion = 1;
    private ClockViewModel? _vm;

    public WidgetManifest Manifest { get; } = new()
    {
        Id = "dev.leedoha.clock.summary",
        Name = "Clock",
        Description = "Current time in a chosen time zone.",
        Version = new Version(1, 0, 0),
        ContractVersion = new Version(1, 0, 0),
        Layout = new WidgetLayoutProfile
        {
            NaturalSize = new(260, 96),
            CompactSize = new(260, 40),
            CollapsedSize = new(260, 24),
            MinNaturalSize = new(160, 96),
            MinCompactSize = new(120, 40),
            MinCollapsedSize = new(60, 24)
        },
        Capabilities = WidgetCapabilities.None,
        AllowMultipleInstances = true,
        SupportsDetailView = true,
        SupportsFloating = true
    };

    private ClockViewModel Vm => _vm ?? throw new InvalidOperationException("Widget is not initialized.");

    public Task InitializeAsync(IWidgetContext context, CancellationToken cancellationToken)
    {
        _vm = new ClockViewModel(new SystemClockService());
        return Task.CompletedTask;
    }

    public object CreateSummaryView(IWidgetContext context) => new ClockSummaryView(Vm);

    public object? CreateDetailView(IWidgetContext context) => new ClockDetailView(Vm);

    public Task SaveStateAsync(IWidgetStateWriter writer)
    {
        writer.Write(StateVersion, JsonSerializer.Serialize(new ClockState(Vm.ZoneId)));
        return Task.CompletedTask;
    }

    public Task RestoreStateAsync(IWidgetStateReader reader)
    {
        if (!reader.TryRead(out var version, out var json)) return Task.CompletedTask;
        // Only one schema so far; future versions migrate here.
        if (version >= 1)
        {
            var state = JsonSerializer.Deserialize<ClockState>(json);
            if (!string.IsNullOrEmpty(state?.ZoneId)) Vm.ZoneId = state.ZoneId;
        }
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _vm?.Dispose();
        _vm = null;
        return Task.CompletedTask;
    }

    private sealed record ClockState(string ZoneId);
}
