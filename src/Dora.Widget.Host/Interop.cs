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

    /// <summary>Cursor position in DIPs of the visual's own monitor scale.</summary>
    public static Point CursorDips(Visual visual)
    {
        var px = CursorPixels();
        var dpi = VisualTreeHelper.GetDpi(visual);
        return new Point(px.X / dpi.DpiScaleX, px.Y / dpi.DpiScaleY);
    }

    /// <summary>Screen bounds (DIP, same space as <see cref="CursorDips"/>) of an element.</summary>
    public static Rect ScreenBounds(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var topLeft = element.PointToScreen(new Point(0, 0));
        return new Rect(topLeft.X / dpi.DpiScaleX, topLeft.Y / dpi.DpiScaleY,
            element.ActualWidth, element.ActualHeight);
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
