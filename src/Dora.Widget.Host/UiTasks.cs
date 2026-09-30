using System.Windows.Threading;

namespace Dora.Widget.Host;

public static class UiTasks
{
    /// <summary>
    /// Waits for <paramref name="task"/> on the UI thread while still pumping messages. A plain
    /// <c>GetAwaiter().GetResult()</c> would deadlock when the task needs the UI thread to continue,
    /// which a widget's async shutdown may well do. Returns false on timeout.
    /// </summary>
    public static bool WaitPumping(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted) return Finish(task);

        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);

        var timer = new DispatcherTimer { Interval = timeout };
        var timedOut = false;
        timer.Tick += (_, _) =>
        {
            timedOut = true;
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();

        return !timedOut && Finish(task);
    }

    private static bool Finish(Task task)
    {
        task.GetAwaiter().GetResult(); // rethrows the task's exception, if any
        return true;
    }
}
