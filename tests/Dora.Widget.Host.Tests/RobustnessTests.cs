using System.IO;
using Dora.Widget.Host;

namespace Dora.Widget.Host.Tests;

public class ErrorReporterTests
{
    [Fact]
    public void Same_error_is_shown_once_per_quiet_period_but_always_logged()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var log = new List<string>();
        var shown = new List<string>();
        var r = new ErrorReporter(log.Add, shown.Add, TimeSpan.FromMinutes(1), () => now);

        Assert.True(r.Report("UI", new InvalidOperationException("boom")));
        Assert.False(r.Report("UI", new InvalidOperationException("boom")));
        Assert.False(r.Report("UI", new InvalidOperationException("boom")));
        Assert.Single(shown);
        Assert.Equal(3, log.Count);
        Assert.Contains("suppressed 2x", log[2]);

        // a different error is new
        Assert.True(r.Report("UI", new InvalidOperationException("other")));
        Assert.Equal(2, shown.Count);

        // after the quiet period the first error may be shown again
        now = now.AddMinutes(2);
        Assert.True(r.Report("UI", new InvalidOperationException("boom")));
        Assert.Equal(3, shown.Count);
    }

    [Fact]
    public void Log_only_mode_and_a_failing_dialog_are_safe()
    {
        var log = new List<string>();
        Assert.False(new ErrorReporter(log.Add, null).Report("x", new Exception("a")));

        var r = new ErrorReporter(log.Add, _ => throw new InvalidOperationException("dialog failed"));
        Assert.True(r.Report("x", new Exception("b")));   // must not throw
    }
}

public class UiTaskTests
{
    [Fact]
    public void WaitPumping_finishes_work_that_needs_the_ui_thread_and_does_not_deadlock()
    {
        Sta.Run(() =>
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));

            async Task NeedsUiThread()
            {
                await Task.Delay(50);   // continuation is posted back to the UI thread
                await Task.Yield();
            }
            Assert.True(UiTasks.WaitPumping(NeedsUiThread(), TimeSpan.FromSeconds(5)));
        });
    }

    [Fact]
    public void WaitPumping_times_out_and_rethrows_task_failures()
    {
        Sta.Run(() =>
        {
            var never = new TaskCompletionSource();
            Assert.False(UiTasks.WaitPumping(never.Task, TimeSpan.FromMilliseconds(150)));

            var failing = Task.FromException(new InvalidOperationException("nope"));
            Assert.Throws<InvalidOperationException>(() => UiTasks.WaitPumping(failing, TimeSpan.FromSeconds(1)));
        });
    }
}

public class LogRotationTests
{
    [Fact]
    public void Large_logs_are_rotated_and_small_ones_left_alone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "md-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = Path.Combine(dir, "host.log");
            File.WriteAllText(log, "small");
            var rotate = typeof(Program).GetMethod("RotateLog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            rotate.Invoke(null, new object[] { log });
            Assert.True(File.Exists(log));
            Assert.False(File.Exists(log + ".1"));

            File.WriteAllText(log, new string('x', 1_100_000));
            rotate.Invoke(null, new object[] { log });
            Assert.False(File.Exists(log));
            Assert.True(File.Exists(log + ".1"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
