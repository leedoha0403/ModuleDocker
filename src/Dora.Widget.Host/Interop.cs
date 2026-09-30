using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Dora.Widget.Runtime;

namespace Dora.Widget.Host;

internal static class Interop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    /// <summary>Cursor position in device pixels.</summary>
    public static Point CursorPixels()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    /// <summary>DPI scale (1.0 = 96 dpi) of the monitor the visual is currently on.</summary>
    public static double Dpi(Visual visual) => VisualTreeHelper.GetDpi(visual).DpiScaleX;

    /// <summary>
    /// Screen bounds of an element in physical pixels. All drag geometry uses pixels so that widgets on
    /// monitors with different DPI (docked Host, floating windows) can be compared directly.
    /// </summary>
    public static Rect ScreenBoundsPx(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var topLeft = element.PointToScreen(new Point(0, 0));
        return new Rect(topLeft.X, topLeft.Y, element.ActualWidth * dpi.DpiScaleX, element.ActualHeight * dpi.DpiScaleY);
    }
}

/// <summary>Runs state machine timers on the WPF dispatcher.</summary>
public sealed class DispatcherTimerScheduler : ITimerScheduler
{
    private sealed class Handle : IDisposable
    {
        public System.Windows.Threading.DispatcherTimer? Timer;
        public void Dispose()
        {
            Timer?.Stop();
            Timer = null;
        }
    }

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var handle = new Handle();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        handle.Timer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (handle.Timer == null) return; // cancelled
            handle.Timer = null;
            callback();
        };
        timer.Start();
        return handle;
    }
}
