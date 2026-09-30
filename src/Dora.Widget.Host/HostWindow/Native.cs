using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Dora.Widget.Abstractions;
using Dora.Widget.Runtime.HostWindow;

namespace Dora.Widget.Host.HostWindow;

/// <summary>Supplies the current monitor layout (physical pixels, taskbar-free work areas, per-monitor DPI).</summary>
public interface IMonitorProvider
{
    IReadOnlyList<MonitorInfo> GetMonitors();
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpNoSendChanging = 0x0400;

    public const int WmSysCommand = 0x0112;
    public const int WmEnterSizeMove = 0x0231;
    public const int WmExitSizeMove = 0x0232;
    public const int WmMoving = 0x0216;
    public const int WmSizing = 0x0214;
    public const int WmDpiChanged = 0x02E0;
    public const int ScSize = 0xF000;
    public const int ScMove = 0xF010;

    public static IReadOnlyList<MonitorInfo> QueryMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref info)) return true;
            uint dpi = 96;
            try { if (GetDpiForMonitor(h, 0, out var dx, out _) == 0 && dx > 0) dpi = dx; } catch (DllNotFoundException) { }
            var w = info.rcWork;
            list.Add(new MonitorInfo(
                info.szDevice,
                new WidgetRect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top),
                dpi / 96.0,
                (info.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WidgetRect GetBoundsPx(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return new WidgetRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static void SetBoundsPx(IntPtr hwnd, WidgetRect b) =>
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(b.X), (int)Math.Round(b.Y),
            Math.Max(1, (int)Math.Round(b.Width)), Math.Max(1, (int)Math.Round(b.Height)),
            SwpNoZOrder | SwpNoActivate);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    /// <summary>Makes an overlay/tool window that never takes focus and (optionally) ignores the mouse.</summary>
    public static void MakeOverlay(Window w, bool clickThrough)
    {
        const int GwlExStyle = -20;
        const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
        var hwnd = Handle(w);
        var style = GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate;
        if (clickThrough) style |= WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();
}

public sealed class SystemMonitorProvider : IMonitorProvider
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var m = Native.QueryMonitors();
        // Never return an empty layout: fall back to the WPF primary work area.
        return m.Count > 0
            ? m
            : new[]
            {
                new MonitorInfo("primary",
                    new WidgetRect(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top,
                        SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height), 1.0, true)
            };
    }
}
