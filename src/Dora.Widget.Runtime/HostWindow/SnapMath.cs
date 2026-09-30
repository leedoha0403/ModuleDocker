using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime.HostWindow;

/// <summary>
/// Pure geometry for edge snapping. Everything is in physical pixels of one monitor's work area;
/// DIP settings are scaled with <see cref="MonitorInfo.DpiScale"/> so a value never means a
/// different physical distance on a high-DPI monitor.
/// </summary>
public static class SnapMath
{
    /// <summary>Nearest allowed edge within the snap distance, or None.</summary>
    public static ScreenEdge DetectEdge(WidgetRect w, MonitorInfo m, double snapDistanceDip, SnapEdges allowed)
    {
        var limit = snapDistanceDip * m.DpiScale;
        var wa = m.WorkArea;
        var best = ScreenEdge.None;
        var bestDist = double.MaxValue;

        void Consider(ScreenEdge edge, SnapEdges flag, double dist)
        {
            if ((allowed & flag) == 0 || dist > limit) return;
            // A window dragged past the edge counts as distance 0, not as "far away".
            dist = Math.Max(dist, 0);
            if (dist < bestDist) { bestDist = dist; best = edge; }
        }

        Consider(ScreenEdge.Left, SnapEdges.Left, w.X - wa.X);
        Consider(ScreenEdge.Right, SnapEdges.Right, wa.X + wa.Width - (w.X + w.Width));
        Consider(ScreenEdge.Top, SnapEdges.Top, w.Y - wa.Y);
        Consider(ScreenEdge.Bottom, SnapEdges.Bottom, wa.Y + wa.Height - (w.Y + w.Height));
        return best;
    }

    /// <summary>Position of the window centre along the edge as 0..1 of the work area length.</summary>
    public static double AlongEdgeRatio(WidgetRect w, MonitorInfo m, ScreenEdge edge)
    {
        var wa = m.WorkArea;
        double r = edge.IsVertical()
            ? (w.Y + w.Height / 2 - wa.Y) / Math.Max(1, wa.Height)
            : (w.X + w.Width / 2 - wa.X) / Math.Max(1, wa.Width);
        return Math.Clamp(r, 0, 1);
    }

    /// <summary>
    /// Bounds of a window of <paramref name="size"/> flush against <paramref name="edge"/>, centred at
    /// <paramref name="ratio"/> along it and never outside the work area (the window shrinks if it is larger).
    /// </summary>
    public static WidgetRect PlaceAtEdge(WidgetSize size, MonitorInfo m, ScreenEdge edge, double ratio)
    {
        var wa = m.WorkArea;
        var w = Math.Min(size.Width, wa.Width);
        var h = Math.Min(size.Height, wa.Height);
        ratio = Math.Clamp(ratio, 0, 1);

        switch (edge)
        {
            case ScreenEdge.Left:
            case ScreenEdge.Right:
            {
                var y = Clamp(wa.Y + ratio * wa.Height - h / 2, wa.Y, wa.Y + wa.Height - h);
                var x = edge == ScreenEdge.Left ? wa.X : wa.X + wa.Width - w;
                return new WidgetRect(x, y, w, h);
            }
            case ScreenEdge.Top:
            case ScreenEdge.Bottom:
            {
                var x = Clamp(wa.X + ratio * wa.Width - w / 2, wa.X, wa.X + wa.Width - w);
                var y = edge == ScreenEdge.Top ? wa.Y : wa.Y + wa.Height - h;
                return new WidgetRect(x, y, w, h);
            }
            default:
                return ClampInside(new WidgetRect(0, 0, w, h), wa);
        }
    }

    /// <summary>Snaps an existing window to an edge, keeping its position along that edge.</summary>
    public static WidgetRect SnapTo(WidgetRect w, MonitorInfo m, ScreenEdge edge) =>
        PlaceAtEdge(new WidgetSize(w.Width, w.Height), m, edge, AlongEdgeRatio(w, m, edge));

    /// <summary>The always-visible strip shown while the Host is hidden.</summary>
    public static WidgetRect HandleBounds(MonitorInfo m, ScreenEdge edge, double thicknessDip, double lengthDip, double ratio)
    {
        var t = thicknessDip * m.DpiScale;
        var wa = m.WorkArea;
        var len = Math.Min(lengthDip * m.DpiScale, edge.IsVertical() ? wa.Height : wa.Width);
        var size = edge.IsVertical() ? new WidgetSize(t, len) : new WidgetSize(len, t);
        // Reuse the edge placement so the handle stays flush and inside the work area.
        return PlaceAtEdge(size, m, edge, ratio);
    }

    /// <summary>
    /// Where a handle dragged to <paramref name="pointer"/> ends up: the nearest allowed edge within
    /// <paramref name="detachDistanceDip"/> (with the new offset ratio), or None to detach into a floating window.
    /// </summary>
    public static (ScreenEdge Edge, double Ratio) ResolveHandleDrag(
        (double X, double Y) pointer, MonitorInfo m, SnapEdges allowed, double detachDistanceDip)
    {
        var wa = m.WorkArea;
        var limit = detachDistanceDip * m.DpiScale;
        var best = ScreenEdge.None;
        var bestDist = double.MaxValue;

        void Consider(ScreenEdge edge, SnapEdges flag, double dist)
        {
            if ((allowed & flag) == 0) return;
            dist = Math.Max(dist, 0);
            if (dist <= limit && dist < bestDist) { bestDist = dist; best = edge; }
        }

        Consider(ScreenEdge.Left, SnapEdges.Left, pointer.X - wa.X);
        Consider(ScreenEdge.Right, SnapEdges.Right, wa.X + wa.Width - pointer.X);
        Consider(ScreenEdge.Top, SnapEdges.Top, pointer.Y - wa.Y);
        Consider(ScreenEdge.Bottom, SnapEdges.Bottom, wa.Y + wa.Height - pointer.Y);

        if (best == ScreenEdge.None) return (ScreenEdge.None, 0);
        var ratio = best.IsVertical()
            ? (pointer.Y - wa.Y) / Math.Max(1, wa.Height)
            : (pointer.X - wa.X) / Math.Max(1, wa.Width);
        return (best, Math.Clamp(ratio, 0, 1));
    }

    /// <summary>Moves a rectangle so it lies fully inside <paramref name="area"/> (shrinks it if larger).</summary>
    public static WidgetRect ClampInside(WidgetRect r, WidgetRect area)
    {
        var w = Math.Min(r.Width, area.Width);
        var h = Math.Min(r.Height, area.Height);
        return new WidgetRect(
            Clamp(r.X, area.X, area.X + area.Width - w),
            Clamp(r.Y, area.Y, area.Y + area.Height - h),
            w, h);
    }

    private static double Clamp(double v, double lo, double hi) => hi < lo ? lo : Math.Min(Math.Max(v, lo), hi);
}
