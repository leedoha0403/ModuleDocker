namespace Dora.Widget.Runtime;

/// <summary>Configurable Host UX values (spec section 18).</summary>
public sealed class HostOptions
{
    public TimeSpan HoverDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan FocusReleaseDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Pointer travel (DIP) before a press becomes a drag.</summary>
    public double DragThreshold { get; set; } = 6;

    /// <summary>Distance (DIP) outside the Host bounds after which a drag detaches.</summary>
    public double DetachMargin { get; set; } = 24;

    /// <summary>Vertical gap between docked widgets (DIP).</summary>
    public double ItemSpacing { get; set; } = 4;
}
