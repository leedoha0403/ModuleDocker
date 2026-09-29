using System.Windows.Input;
using Dora.Widget.Abstractions;
using Dora.Widget.SDK;

namespace Counter.Widget;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}

public sealed class CounterViewModel : WidgetViewModelBase
{
    private int _count;

    public CounterViewModel()
    {
        IncrementCommand = new RelayCommand(() => Count++);
        DecrementCommand = new RelayCommand(() => Count--);
    }

    public int Count { get => _count; set => SetField(ref _count, value); }
    public ICommand IncrementCommand { get; }
    public ICommand DecrementCommand { get; }
}

public partial class CounterSummaryView : ModeTemplateView
{
    public CounterSummaryView(CounterViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Content = vm;
        OnDisplayModeChanged(vm.DisplayMode);
    }
}

/// <summary>Minimal widget: no detail view, docked only, single instance.</summary>
public sealed class CounterWidget : IComposableWidget
{
    private readonly CounterViewModel _vm = new();

    public WidgetManifest Manifest { get; } = new()
    {
        Id = "dev.leedoha.counter.summary",
        Name = "Counter",
        Version = new Version(1, 0, 0),
        ContractVersion = new Version(1, 0, 0),
        Layout = new WidgetLayoutProfile
        {
            NaturalSize = new(260, 56),
            CompactSize = new(260, 32),
            CollapsedSize = new(260, 22),
            MinNaturalSize = new(160, 56),
            MinCompactSize = new(100, 32),
            MinCollapsedSize = new(50, 22)
        },
        Capabilities = WidgetCapabilities.None,
        AllowMultipleInstances = false,
        SupportsDetailView = false,
        SupportsFloating = false
    };

    public Task InitializeAsync(IWidgetContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public object CreateSummaryView(IWidgetContext context) => new CounterSummaryView(_vm);

    public object? CreateDetailView(IWidgetContext context) => null;

    public Task SaveStateAsync(IWidgetStateWriter writer)
    {
        writer.Write(1, _vm.Count.ToString());
        return Task.CompletedTask;
    }

    public Task RestoreStateAsync(IWidgetStateReader reader)
    {
        if (reader.TryRead(out _, out var json) && int.TryParse(json, out var n)) _vm.Count = n;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
