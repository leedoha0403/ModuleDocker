using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime.HostWindow;

/// <summary>Monitor selection and restoring saved placement when monitors or DPI changed.</summary>
public static class MonitorMath
{
    /// <summary>Monitor with the largest overlap; otherwise the one whose work area is nearest to the rectangle.</summary>
    public static MonitorInfo Pick(IReadOnlyList<MonitorInfo> monitors, WidgetRect r)
    {
        if (monitors.Count == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));

        MonitorInfo? best = null;
        var bestArea = 0d;
        foreach (var m in monitors)
        {
            var a = OverlapArea(r, m.WorkArea);
            if (a > bestArea) { bestArea = a; best = m; }
        }
        if (best != null) return best;

        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;
        return monitors.OrderBy(m => DistanceToRect(cx, cy, m.WorkArea)).First();
    }

    /// <summary>The saved monitor, or the primary monitor when it is no longer connected.</summary>
    public static MonitorInfo Find(IReadOnlyList<MonitorInfo> monitors, string? id)
    {
        if (monitors.Count == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        return monitors.FirstOrDefault(m => id != null && m.Id == id)
               ?? monitors.FirstOrDefault(m => m.IsPrimary)
               ?? monitors[0];
    }

    public sealed record Restored(HostWindowState State, MonitorInfo Monitor, WidgetRect Bounds);

    /// <summary>
    /// Turns saved settings into a concrete initial state and pixel bounds for the current monitor layout.
    /// </summary>
    /// <param name="hostSizeDip">Host size in DIPs; converted with the target monitor's DPI.</param>
    public static Restored Restore(HostWindowSettings settings, IReadOnlyList<MonitorInfo> monitors, WidgetSize hostSizeDip)
    {
        var s = settings.Sanitized();
        var monitor = Find(monitors, s.MonitorId);
        var sizePx = new WidgetSize(hostSizeDip.Width * monitor.DpiScale, hostSizeDip.Height * monitor.DpiScale);

        if (s.PlacementState == HostPlacementState.Snapped)
        {
            var bounds = SnapMath.PlaceAtEdge(sizePx, monitor, s.SnappedEdge, s.HandleOffsetRatio);
            return new Restored(HostWindowState.SnappedVisible, monitor, bounds);
        }

        if (s.FloatingBounds is { } fb)
        {
            var wa = monitor.WorkArea;
            var ratio = monitor.DpiScale / s.FloatingDpiScale;
            var sameMonitor = s.MonitorId != null && s.MonitorId == monitor.Id;

            double x, y;
            if (sameMonitor)
            {
                // Keep the offset within the monitor, rescaled if its DPI changed.
                x = wa.X + (fb.X - wa.X) * ratio;
                y = wa.Y + (fb.Y - wa.Y) * ratio;
            }
            else
            {
                // The monitor is gone: centre the window on the fallback monitor.
                x = wa.X + (wa.Width - fb.Width * ratio) / 2;
                y = wa.Y + (wa.Height - fb.Height * ratio) / 2;
            }
            var rect = SnapMath.ClampInside(new WidgetRect(x, y, fb.Width * ratio, fb.Height * ratio), wa);
            return new Restored(HostWindowState.Floating, monitor, rect);
        }

        var centred = SnapMath.ClampInside(new WidgetRect(
            monitor.WorkArea.X + (monitor.WorkArea.Width - sizePx.Width) / 2,
            monitor.WorkArea.Y + (monitor.WorkArea.Height - sizePx.Height) / 2,
            sizePx.Width, sizePx.Height), monitor.WorkArea);
        return new Restored(HostWindowState.Floating, monitor, centred);
    }

    private static double OverlapArea(WidgetRect a, WidgetRect b)
    {
        var w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    private static double DistanceToRect(double x, double y, WidgetRect r)
    {
        var dx = Math.Max(Math.Max(r.X - x, 0), x - (r.X + r.Width));
        var dy = Math.Max(Math.Max(r.Y - y, 0), y - (r.Y + r.Height));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
