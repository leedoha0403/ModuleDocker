using System.Windows.Threading;
using Dora.Widget.Host;

namespace Dora.Widget.Host.Tests;

public class WatchdogTests
{
    [Fact]
    public void Reports_a_blocked_ui_thread_once_and_reports_recovery()
    {
        Sta.Run(() =>
        {
            var lines = new List<string>();
            using var dog = new UiWatchdog(Dispatcher.CurrentDispatcher, l => { lock (lines) lines.Add(l); },
                threshold: TimeSpan.FromMilliseconds(200), interval: TimeSpan.FromMilliseconds(40));

            Sta.PumpFor(300); // healthy: nothing to report
            lock (lines) Assert.Empty(lines);

            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => Thread.Sleep(700)));
            Sta.PumpFor(1400);

            lock (lines)
            {
                Assert.Single(lines, l => l.Contains("unresponsive"));
                Assert.Contains(lines, l => l.Contains("responsive again"));
            }
        });
    }

    [Fact]
    public void Dispose_stops_the_thread_even_when_the_dispatcher_is_gone()
    {
        Sta.Run(() =>
        {
            var dog = new UiWatchdog(Dispatcher.CurrentDispatcher, _ => { }, interval: TimeSpan.FromMilliseconds(20));
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            dog.Dispose(); // must not hang or throw
        });
    }
}
