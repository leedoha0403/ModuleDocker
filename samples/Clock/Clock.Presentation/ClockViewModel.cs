using System.Windows.Threading;
using Clock.Core;
using Dora.Widget.SDK;

namespace Clock.Presentation;

/// <summary>Single feature state shared by the docked, floating and detail surfaces.</summary>
public sealed class ClockViewModel : WidgetViewModelBase, IDisposable
{
    private readonly IClockService _clock;
    private readonly DispatcherTimer _timer;
    private readonly Func<DateTimeOffset> _utcNow;
    private string _zoneId;
    private ClockReading _reading;

    public ClockViewModel(IClockService clock, string? zoneId = null, Func<DateTimeOffset>? utcNow = null)
    {
        _clock = clock;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _zoneId = zoneId ?? TimeZoneInfo.Local.Id;
        _reading = _clock.Read(_zoneId, _utcNow());

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public IReadOnlyList<(string Id, string Name)> Zones => _clock.Zones;

    public string ZoneId
    {
        get => _zoneId;
        set
        {
            if (!SetField(ref _zoneId, value)) return;
            Tick();
        }
    }

    public string Time => _reading.Time;
    public string ShortTime => _reading.ShortTime;
    public string Date => _reading.Date;
    public string ZoneName => _reading.ZoneName;

    public void Tick()
    {
        _reading = _clock.Read(_zoneId, _utcNow());
        Raise(nameof(Time));
        Raise(nameof(ShortTime));
        Raise(nameof(Date));
        Raise(nameof(ZoneName));
    }

    public void Dispose() => _timer.Stop();
}
