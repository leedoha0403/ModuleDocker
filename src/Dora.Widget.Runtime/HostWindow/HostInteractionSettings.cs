namespace Dora.Widget.Runtime.HostWindow;

/// <summary>
/// Host-wide interaction tuning (hover/release delays, drag threshold, spacing). These are Host preferences:
/// widgets cannot change them (spec: "these values should remain configurable by the Host").
/// </summary>
public sealed record HostInteractionSettings
{
    public int HoverDelayMs { get; init; } = 200;
    public int FocusReleaseDelayMs { get; init; } = 300;

    /// <summary>Pointer travel (DIP) before a press becomes a drag.</summary>
    public double DragThreshold { get; init; } = 6;

    /// <summary>How far outside the Host (DIP) a drag must go before it detaches.</summary>
    public double DetachMargin { get; init; } = 24;

    /// <summary>Vertical gap between docked widgets (DIP).</summary>
    public double ItemSpacing { get; init; } = 4;

    public HostInteractionSettings Sanitized() => this with
    {
        HoverDelayMs = Math.Clamp(HoverDelayMs, 0, 2000),
        FocusReleaseDelayMs = Math.Clamp(FocusReleaseDelayMs, 0, 5000),
        DragThreshold = Clamp(DragThreshold, 1, 50, 6),
        DetachMargin = Clamp(DetachMargin, 0, 200, 24),
        ItemSpacing = Clamp(ItemSpacing, 0, 32, 4)
    };

    /// <summary>Copies the values into the live options object the state machines and layout read from.</summary>
    public void ApplyTo(HostOptions options)
    {
        var s = Sanitized();
        options.HoverDelay = TimeSpan.FromMilliseconds(s.HoverDelayMs);
        options.FocusReleaseDelay = TimeSpan.FromMilliseconds(s.FocusReleaseDelayMs);
        options.DragThreshold = s.DragThreshold;
        options.DetachMargin = s.DetachMargin;
        options.ItemSpacing = s.ItemSpacing;
    }

    private static double Clamp(double v, double lo, double hi, double fallback) =>
        double.IsNaN(v) || double.IsInfinity(v) ? fallback : Math.Clamp(v, lo, hi);
}
