using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using DiskUsage.Core;
using Dora.Widget.SDK;

namespace DiskUsage.Presentation;

public sealed class DriveItem : WidgetViewModelBase
{
    private double _usedPercent;
    private string _usage = "";

    public DriveItem(string name) => Name = name;

    public string Name { get; }
    public string DisplayName { get; private set; } = "";
    public double UsedPercent { get => _usedPercent; private set => SetField(ref _usedPercent, value); }
    public string Usage { get => _usage; private set => SetField(ref _usage, value); }
    public bool IsCritical => UsedPercent >= DiskViewModel.CriticalPercent;

    public void Update(DriveReading r)
    {
        DisplayName = r.DisplayName;
        UsedPercent = r.UsedPercent;
        Usage = $"{ByteFormat.Format(r.UsedBytes)} / {ByteFormat.Format(r.TotalBytes)}  ·  free {ByteFormat.Format(r.FreeBytes)}";
        Raise(nameof(DisplayName));
        Raise(nameof(IsCritical));
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}

/// <summary>Single disk feature state shared by the docked, floating and detail surfaces.</summary>
public sealed class DiskViewModel : WidgetViewModelBase, IDisposable
{
    /// <summary>Used share (percent) from which a drive counts as critical.</summary>
    public const double CriticalPercent = 90;

    private readonly IDiskService _disks;
    private readonly DispatcherTimer? _timer;
    private DriveItem? _selected;
    private string _status = "";
    private bool _hasCritical;

    public DiskViewModel(IDiskService disks, TimeSpan? refreshEvery = null)
    {
        _disks = disks;
        RefreshCommand = new RelayCommand(Refresh);
        Refresh();

        if (refreshEvery is { } every)
        {
            _timer = new DispatcherTimer { Interval = every };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
        }
    }

    public ObservableCollection<DriveItem> Drives { get; } = new();
    public ICommand RefreshCommand { get; }

    /// <summary>Raised when the critical state changes (true = at least one drive is critical).</summary>
    public event Action<bool>? CriticalChanged;

    /// <summary>Raised when the user picks a drive, e.g. to publish <c>filesystem.path.selected</c>.</summary>
    public event Action<string>? DriveSelected;

    public DriveItem? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            if (value != null) DriveSelected?.Invoke(value.Name + "\\");
        }
    }

    /// <summary>The fullest drive, shown in the compact and collapsed views.</summary>
    public DriveItem? Worst => Drives.OrderByDescending(d => d.UsedPercent).FirstOrDefault();

    public string WorstText => Worst is { } w ? $"{w.Name} {w.UsedPercent:0}%" : "no disks";

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public bool HasCritical => _hasCritical;

    /// <summary>Restores the previously selected drive without raising <see cref="DriveSelected"/>.</summary>
    public void SelectSilently(string? name)
    {
        var item = Drives.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        _selected = item;
        Raise(nameof(Selected));
    }

    public void Refresh()
    {
        IReadOnlyList<DriveReading> readings;
        try { readings = _disks.Read(); }
        catch (Exception ex)
        {
            Status = "Could not read disks: " + ex.Message;
            return;
        }

        // Update in place so selection and bindings survive a refresh.
        foreach (var gone in Drives.Where(d => readings.All(r => r.Name != d.Name)).ToList())
        {
            if (ReferenceEquals(gone, _selected)) { _selected = null; Raise(nameof(Selected)); }
            Drives.Remove(gone);
        }
        foreach (var r in readings)
        {
            var item = Drives.FirstOrDefault(d => d.Name == r.Name);
            if (item == null) Drives.Add(item = new DriveItem(r.Name));
            item.Update(r);
        }

        Status = readings.Count == 0 ? "No disks found" : $"Updated {DateTime.Now:HH:mm:ss}";
        Raise(nameof(Worst));
        Raise(nameof(WorstText));

        var critical = Drives.Any(d => d.IsCritical);
        if (critical != _hasCritical)
        {
            _hasCritical = critical;
            Raise(nameof(HasCritical));
            CriticalChanged?.Invoke(critical);
        }
    }

    public void Dispose() => _timer?.Stop();
}
