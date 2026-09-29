namespace Dora.Widget.Runtime;

/// <summary>Abstraction over timers so the state machine is testable and UI-thread agnostic.</summary>
public interface ITimerScheduler
{
    /// <summary>Runs <paramref name="callback"/> once after <paramref name="delay"/>. Dispose to cancel.</summary>
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>Deterministic scheduler driven by <see cref="Advance"/>; used by tests and simulations.</summary>
public sealed class ManualTimerScheduler : ITimerScheduler
{
    private sealed class Entry : IDisposable
    {
        public TimeSpan Due;
        public Action? Callback;
        public void Dispose() => Callback = null;
    }

    private readonly List<Entry> _entries = new();
    private TimeSpan _now = TimeSpan.Zero;

    public int PendingCount => _entries.Count(e => e.Callback != null);

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var entry = new Entry { Due = _now + delay, Callback = callback };
        _entries.Add(entry);
        return entry;
    }

    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            var next = _entries
                .Where(e => e.Callback != null && e.Due <= target)
                .OrderBy(e => e.Due)
                .FirstOrDefault();
            if (next == null) break;
            _now = next.Due;
            var cb = next.Callback!;
            next.Callback = null;
            cb();
        }
        _now = target;
        _entries.RemoveAll(e => e.Callback == null);
    }
}
