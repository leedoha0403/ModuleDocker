using System.Diagnostics;
using System.Windows.Threading;

namespace Dora.Widget.Host;

/// <summary>
/// Widgets run inside the Host process, so a widget that blocks the UI thread freezes the whole Host.
/// This cannot prevent that, but it records when and for how long it happened so the cause can be found.
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _threshold;
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastAckTicks;
    private volatile bool _stop;
    private bool _reported;

    public UiWatchdog(Dispatcher dispatcher, Action<string> log, TimeSpan? threshold = null, TimeSpan? interval = null)
    {
        _dispatcher = dispatcher;
        _log = log;
        _threshold = threshold ?? TimeSpan.FromSeconds(5);
        _interval = interval ?? TimeSpan.FromSeconds(1);
        Volatile.Write(ref _lastAckTicks, _clock.ElapsedTicks);
        _thread = new Thread(Run) { IsBackground = true, Name = "ModuleDock UI watchdog" };
        _thread.Start();
    }

    private TimeSpan SinceAck => TimeSpan.FromSeconds(
        (double)(_clock.ElapsedTicks - Volatile.Read(ref _lastAckTicks)) / Stopwatch.Frequency);

    private void Run()
    {
        while (!_stop)
        {
            try
            {
                // Low priority: only answered when the UI thread is actually idle enough to process input.
                _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    Volatile.Write(ref _lastAckTicks, _clock.ElapsedTicks)));
            }
            catch (Exception) { return; } // dispatcher is shutting down

            Thread.Sleep(_interval);

            var late = SinceAck;
            if (late >= _threshold && !_reported)
            {
                _reported = true;
                _log($"[Warn] UI thread unresponsive for {late.TotalSeconds:0.0}s (a widget or the Host is blocking it)");
            }
            else if (late < _threshold && _reported)
            {
                _reported = false;
                _log("[Info] UI thread responsive again");
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(_interval + TimeSpan.FromMilliseconds(500));
    }
}
