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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Makes the system title bar follow the Host theme now and on every later theme change (Windows 10 1809+ / 11).</summary>
    public static void FollowTheme(Window window)
    {
        void Apply()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var on = HostTheme.IsDark ? 1 : 0;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (19 on the earliest builds that support it)
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        Action changed = () => window.Dispatcher.BeginInvoke(Apply);
        window.SourceInitialized += (_, _) => { Apply(); HostTheme.Changed += changed; };
        window.Closed += (_, _) => HostTheme.Changed -= changed;
    }

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
