namespace Dora.Widget.Abstractions;

/// <summary>UI-framework neutral point (physical pixels on the virtual screen).</summary>
public readonly record struct WidgetPoint(double X, double Y);

/// <summary>Asks a widget's external surface to take over a widget that was dragged out of the Host.</summary>
/// <param name="InstanceId">The Host instance being handed over (it is removed once the hand-over succeeds).</param>
/// <param name="StateVersion">Schema version of <paramref name="StateJson"/> (what SaveStateAsync wrote).</param>
/// <param name="StateJson">The widget's persisted feature state, so the external surface continues where it left off.</param>
/// <param name="ScreenBounds">Where the widget was dropped, virtual-screen physical pixels.</param>
/// <param name="Dpi">Dpi of the monitor the bounds belong to.</param>
public sealed record DetachRequest(string InstanceId, int StateVersion, string StateJson, WidgetRect ScreenBounds, double Dpi);

/// <summary>The external surface asks to become a docked Host widget again.</summary>
/// <param name="StateVersion">Schema version of <paramref name="StateJson"/>.</param>
/// <param name="StateJson">The state to continue from, in the widget's own format.</param>
/// <param name="ScreenCursor">Where the drop happened, virtual-screen physical pixels (picks the dock slot).</param>
public sealed record DockRequest(int StateVersion, string StateJson, WidgetPoint ScreenCursor);

/// <summary>
/// Optional. A widget that also exists as a separate application (its "original process") supplies one of these so
/// that dragging it out of the Host hands ownership to that application, and dropping the application's window
/// onto the Host hands it back. Exactly one side owns the widget at any time; the one that receives the state and
/// acknowledges it becomes the owner. Discovered by the plugin loader next to the widget (public, parameterless
/// constructor) and alive for the Host's lifetime.
/// </summary>
public interface IWidgetDetachHandler
{
    /// <summary>The widget type this handler belongs to (<see cref="WidgetManifest.Id"/>).</summary>
    string WidgetId { get; }

    /// <summary>True while the external surface owns the widget.</summary>
    bool IsDetached { get; }

    /// <summary>
    /// Hands the widget to the external surface. Returns true only after the other side has applied the state
    /// and shown itself; false leaves the Host as the owner (the Host then falls back to its own floating window).
    /// </summary>
    Task<bool> DetachAsync(DetachRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Raised when the external surface wants to dock. The Host creates the instance from the state and returns
    /// whether it accepted; only an accepted request ends the external surface's ownership.
    /// </summary>
    event Func<DockRequest, Task<bool>>? DockRequested;

    /// <summary>The external surface is being dragged over the Host at this point (null when it left).</summary>
    event Action<WidgetPoint?>? DockHover;

    /// <summary>The external surface went away without docking (closed or crashed); nothing is detached any more.</summary>
    event Action? DetachEnded;

    Task ShutdownAsync();

    /// <summary>
    /// Asks the handler to look for an external surface that was started on its own (not handed over by the
    /// Host) and connect to it, so that surface can also be dropped onto the Host. Called when the Host starts
    /// and again whenever an application announces itself (<see cref="WidgetAnnounce"/>). Must be idempotent and
    /// must not launch anything. Optional: the default does nothing.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Well-known signal for widget applications: set this named event when the application starts. A running Host
/// then asks its hand-over handlers to look for it (<see cref="IWidgetDetachHandler.StartAsync"/>); with no Host
/// running the event does not exist and opening it simply fails. Nothing polls.
/// </summary>
public static class WidgetAnnounce
{
    public const string EventName = "Local\\ModuleDock.WidgetAnnounce";

    /// <summary>
    /// Event an application signals to have the Host show the widget's detail window instead of opening a second
    /// main screen of its own while the Host owns the widget.
    /// </summary>
    public static string OpenDetailEventName(string widgetId, string announceEventName = EventName) =>
        announceEventName + ".OpenDetail." + widgetId;
}
