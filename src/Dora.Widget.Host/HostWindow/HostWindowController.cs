using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Dora.Widget.Abstractions;
using Dora.Widget.Runtime;
using Dora.Widget.Runtime.HostWindow;

namespace Dora.Widget.Host.HostWindow;

/// <summary>Counts open Host-owned popups (context menus, dialogs) so auto-hide can wait for them.</summary>
public sealed class HostPopupTracker
{
    private int _open;

    public bool AnyOpen => _open > 0;
    public event Action<bool>? Changed;

    public void Track(ContextMenu menu)
    {
        menu.Opened += (_, _) => Push();
        menu.Closed += (_, _) => Pop();
    }

    /// <summary>For modal dialogs shown with the Host as owner: <c>using (tracker.Scope()) MessageBox.Show(...)</c>.</summary>
    public IDisposable Scope()
    {
        Push();
        return new Popper(this);
    }

    private void Push()
    {
        _open++;
        if (_open == 1) Changed?.Invoke(true);
    }

    private void Pop()
    {
        if (_open == 0) return;
        _open--;
        if (_open == 0) Changed?.Invoke(false);
    }

    private sealed class Popper : IDisposable
    {
        private HostPopupTracker? _t;
        public Popper(HostPopupTracker t) => _t = t;
        public void Dispose() => Interlocked.Exchange(ref _t, null)?.Pop();
    }
}

/// <summary>
/// Connects the main Host window to <see cref="HostWindowStateMachine"/>: edge snapping with a magnetic
/// pull, auto-hide with a handle strip, reveal, handle dragging and multi-monitor/DPI aware restore.
/// All window geometry is handled in physical pixels; DIP settings are scaled per monitor.
/// Widget code never sees any of this.
/// </summary>
public sealed class HostWindowController : IDisposable
{
    /// <summary>Length of the handle strip along the edge (DIP).</summary>
    public const double HandleLengthDip = 92;

    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(220);

    private enum PendingKind { None, Move, Resize }

    private readonly Window _host;
    private readonly IMonitorProvider _monitors;
    private readonly ITimerScheduler _timers;
    private readonly Action<HostWindowSettings>? _persist;
    private readonly HandleWindow _handle = new();
    private readonly SnapPreviewWindow _preview = new();
    private readonly DispatcherTimer _pointerPoll;
    private HwndSource? _source;
    private PendingKind _pending;
    private WidgetRect _visibleBounds;      // last bounds while the Host was visible (px)
    private WidgetSize _hostSizeDip;
    private int _animationVersion;
    private bool _animating;
    private bool _minRelaxed;
    private double _minWidth, _minHeight;
    private bool _userTopmost;
    private WidgetRect _handleDragStartBounds;
    private bool _disposed;

    public HostWindowController(
        Window host,
        HostWindowSettings settings,
        IMonitorProvider monitors,
        ITimerScheduler timers,
        Action<HostWindowSettings>? persist = null)
    {
        _host = host;
        _monitors = monitors;
        _timers = timers;
        _persist = persist;

        var s = settings.Sanitized();
        _hostSizeDip = s.HostSizeDip ?? new WidgetSize(host.Width, host.Height);
        Machine = new HostWindowStateMachine(s, timers);
        Machine.StateChanged += OnStateChanged;
        Machine.SettingsChanged += OnSettingsChanged;

        _host.ShowActivated = false;
        // Focus is only an auxiliary signal for the state machine; it never triggers a hide by itself.
        _host.Activated += (_, _) => Machine.SetHasFocus(true);
        _host.Deactivated += (_, _) => Machine.SetHasFocus(false);
        _host.SizeChanged += (_, _) =>
        {
            if (_animating || !_host.IsVisible || Machine.State is HostWindowState.Resizing) return;
            _hostSizeDip = new WidgetSize(_host.ActualWidth, _host.ActualHeight);
        };

        _handle.PointerEntered += Machine.PointerEnteredHandle;
        _handle.PointerLeft += Machine.PointerLeftHandle;
        // Pressing the strip means the user wants to click or drag it, not to wait for the hover reveal.
        _handle.PressStarted += Machine.PointerLeftHandle;
        _handle.Clicked += Machine.Reveal;
        _handle.DragStarted += OnHandleDragStarted;
        _handle.DragMoved += OnHandleDragMoved;
        _handle.DragEnded += OnHandleDragEnded;

        _pointerPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _pointerPoll.Tick += (_, _) => UpdatePointerInside();
    }

    public HostWindowStateMachine Machine { get; }
    public HandleWindow Handle => _handle;

    /// <summary>Slide the Host in/out and keep animations off in tests.</summary>
    public bool AnimationsEnabled { get; set; } = true;

    /// <summary>Snap the window to the edge while it is being dragged inside the snap distance.</summary>
    public bool MagneticPull { get; set; } = true;

    /// <summary>Cursor position source (physical pixels); replaced in tests.</summary>
    public Func<Point> CursorPx { get; set; } = Interop.CursorPixels;

    /// <summary>The user's own "keep on top" choice; a snapped Host is always on top.</summary>
    public bool UserTopmost
    {
        get => _userTopmost;
        set { _userTopmost = value; ApplyTopmost(); }
    }

    public WidgetSize HostSizeDip => _hostSizeDip;
    public WidgetRect VisibleBounds => _visibleBounds;

    // ---- lifecycle -----------------------------------------------------------------------------

    /// <summary>Places the Host from the saved settings; call once before showing the window.</summary>
    public void Initialize()
    {
        var hwnd = Native.Handle(_host);
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        var monitors = _monitors.GetMonitors();
        _userTopmost = Machine.Settings.AlwaysOnTop;
        var restored = MonitorMath.Restore(Machine.Settings, monitors, _hostSizeDip);
        _visibleBounds = restored.Bounds;
        Native.SetBoundsPx(hwnd, restored.Bounds);
        ApplyTopmost();
        UpdatePointerPolling();
    }

    /// <summary>Starts hidden if the persisted state says so (auto-hide is evaluated by the machine).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pointerPoll.Stop();
        _source?.RemoveHook(WndProc);
        _handle.Close();
        _preview.Close();
    }

    // ---- busy sources ------------------------------------------------------------------------------

    /// <summary>Feeds the "pointer inside the Host" condition, including the title bar and borders.</summary>
    public void UpdatePointerInside()
    {
        // Dialogs and tool windows owned by the Host keep it open while they are showing.
        Machine.SetBusy(HostBusy.OwnedPopupOpen, _host.OwnedWindows.Cast<Window>().Any(w => w.IsVisible));
        if (!_host.IsVisible) { Machine.SetBusy(HostBusy.PointerInsideHost, false); return; }
        var b = Native.GetBoundsPx(Native.Handle(_host));
        var p = CursorPx();
        var inside = p.X >= b.X && p.X < b.X + b.Width && p.Y >= b.Y && p.Y < b.Y + b.Height;
        Machine.SetBusy(HostBusy.PointerInsideHost, inside);
    }

    public void SetPopupOpen(bool open) => Machine.SetBusy(HostBusy.ContextMenuOpen, open);

    /// <summary>Widget drag / drop preview activity coming from the docked area.</summary>
    public void SetWidgetDragActivity(bool dragging, bool dropPreview)
    {
        Machine.SetBusy(HostBusy.WidgetDragging, dragging);
        Machine.SetBusy(HostBusy.DropPreviewShown, dropPreview);
    }

    private void UpdatePointerPolling()
    {
        var need = Machine.Settings.AutoHideEnabled && Machine.Placement == HostPlacementState.Snapped;
        if (need && !_pointerPoll.IsEnabled) _pointerPoll.Start();
        else if (!need && _pointerPoll.IsEnabled) _pointerPoll.Stop();
    }

    // ---- state -> window ---------------------------------------------------------------------------------

    private MonitorInfo CurrentMonitor(IReadOnlyList<MonitorInfo> monitors) =>
        MonitorMath.Find(monitors, Machine.Settings.MonitorId);

    private void OnSettingsChanged()
    {
        UpdatePointerPolling();
        // A changed handle thickness / edge must show up on a handle that is already on screen.
        if (_handle.IsVisible && Machine.Settings.SnappedEdge != ScreenEdge.None)
            _handle.Place(HandleRect(_monitors.GetMonitors()), Machine.Settings.SnappedEdge);
        if (_userTopmost != Machine.Settings.AlwaysOnTop) UserTopmost = Machine.Settings.AlwaysOnTop;
        _persist?.Invoke(Machine.Settings with { HostSizeDip = _hostSizeDip });
    }

    /// <summary>
    /// Fold button (like the AIUsage Monitor widget): turns auto-hide on and slides the Host away at once, or
    /// turns it off again so the Host stays out. Only meaningful while snapped.
    /// </summary>
    public void ToggleFold()
    {
        if (Machine.Placement != HostPlacementState.Snapped) return;
        var s = Machine.Settings;
        if (!s.AutoHideEnabled)
        {
            Machine.ApplySettings(s with { AutoHideEnabled = true });
            Machine.HideNow(force: true);     // the pointer is on the button, so busy conditions must not veto this
        }
        else
        {
            Machine.ApplySettings(s with { AutoHideEnabled = false });
        }
    }

    private void OnStateChanged(HostWindowState old, HostWindowState now)
    {
        UpdatePointerPolling();
        switch (now)
        {
            case HostWindowState.SnappedHidden:
                _handle.Active = false;
                HideHost();
                break;

            case HostWindowState.RevealPending:
                _handle.Active = true;
                break;

            case HostWindowState.SnappedVisible:
                _handle.Active = false;
                // Coming back from hidden (also mid-way through the hide slide): slide out from the edge.
                if (old is HostWindowState.SnappedHidden or HostWindowState.RevealPending) ShowHostFromEdge();
                _handle.Hide();
                ApplyTopmost();
                UpdatePointerInside();
                break;

            case HostWindowState.Floating:
                _handle.Hide();
                if (!_host.IsVisible) _host.Show();
                ApplyTopmost();
                break;
        }
    }

    private void ApplyTopmost() =>
        _host.Topmost = _userTopmost || Machine.Placement == HostPlacementState.Snapped;

    private WidgetRect HandleRect(IReadOnlyList<MonitorInfo> monitors)
    {
        var m = CurrentMonitor(monitors);
        var s = Machine.Settings;
        return SnapMath.HandleBounds(m, s.SnappedEdge, s.HandleThickness, HandleLengthDip, s.HandleOffsetRatio);
    }

    private void ShowHandle()
    {
        var monitors = _monitors.GetMonitors();
        var appearing = !_handle.IsVisible;
        _handle.Place(HandleRect(monitors), Machine.Settings.SnappedEdge);
        if (appearing) _handle.Show();
        // Placing before Show is not enough for a window that has just been created.
        _handle.Place(HandleRect(monitors), Machine.Settings.SnappedEdge);
        if (appearing) _handle.Pulse();
    }

    /// <summary>The window's MinWidth/MinHeight would stop it collapsing to the handle strip, so lift them during a slide.</summary>
    private void RelaxMinSize()
    {
        if (_minRelaxed) return;
        _minWidth = _host.MinWidth;
        _minHeight = _host.MinHeight;
        _host.MinWidth = 0;
        _host.MinHeight = 0;
        _minRelaxed = true;
    }

    private void RestoreMinSize()
    {
        if (!_minRelaxed) return;
        _host.MinWidth = _minWidth;
        _host.MinHeight = _minHeight;
        _minRelaxed = false;
    }

    private static WidgetRect Collapsed(WidgetRect v, ScreenEdge edge, double thicknessPx) => edge switch
    {
        ScreenEdge.Left => new WidgetRect(v.X, v.Y, thicknessPx, v.Height),
        ScreenEdge.Right => new WidgetRect(v.X + v.Width - thicknessPx, v.Y, thicknessPx, v.Height),
        ScreenEdge.Top => new WidgetRect(v.X, v.Y, v.Width, thicknessPx),
        _ => new WidgetRect(v.X, v.Y + v.Height - thicknessPx, v.Width, thicknessPx)
    };

    private void HideHost()
    {
        var hwnd = Native.Handle(_host);
        var monitors = _monitors.GetMonitors();
        var monitor = CurrentMonitor(monitors);
        var edge = Machine.Settings.SnappedEdge;
        if (_host.IsVisible && !_animating) _visibleBounds = Native.GetBoundsPx(hwnd);
        var visible = _visibleBounds;
        var strip = HiddenTarget(visible, edge, monitor, monitors, Machine.Settings.HandleThickness * monitor.DpiScale);

        void Finish()
        {
            if (Machine.State is not (HostWindowState.SnappedHidden or HostWindowState.RevealPending)) return;
            _host.Hide();
            Native.SetBoundsPx(hwnd, visible); // remember the visible size for the next reveal
            ShowHandle();
        }

        if (!AnimationsEnabled || !_host.IsVisible) { Finish(); return; }
        Animate(hwnd, visible, strip, SlideDuration, Finish);
    }

    private void ShowHostFromEdge()
    {
        var hwnd = Native.Handle(_host);
        var monitors = _monitors.GetMonitors();
        var monitor = CurrentMonitor(monitors);
        var s = Machine.Settings;
        var sizePx = new WidgetSize(_hostSizeDip.Width * monitor.DpiScale, _hostSizeDip.Height * monitor.DpiScale);
        var target = SnapMath.PlaceAtEdge(sizePx, monitor, s.SnappedEdge, s.HandleOffsetRatio);
        _visibleBounds = target;

        if (!AnimationsEnabled)
        {
            Native.SetBoundsPx(hwnd, target);
            if (!_host.IsVisible) _host.Show();
            return;
        }

        // Start from wherever the window is if a hide slide was interrupted, else from the collapsed strip.
        var from = _host.IsVisible && _animating
            ? Native.GetBoundsPx(hwnd)
            : HiddenTarget(target, s.SnappedEdge, monitor, monitors, s.HandleThickness * monitor.DpiScale);
        Native.SetBoundsPx(hwnd, from);
        if (!_host.IsVisible) _host.Show();
        Animate(hwnd, from, target, SlideDuration, () => Native.SetBoundsPx(hwnd, target));
    }

    /// <summary>
    /// Where the Host ends up when hidden. Like the AIUsage Monitor widget, the whole window slides out past its
    /// screen edge. If another monitor lies beyond that edge the window would show up on it, so there the window
    /// instead shrinks toward its edge (a curtain) and never leaves its own monitor.
    /// </summary>
    private static WidgetRect HiddenTarget(WidgetRect visible, ScreenEdge edge, MonitorInfo monitor,
        IReadOnlyList<MonitorInfo> all, double thicknessPx)
    {
        if (HasNeighbor(all, monitor, edge, visible)) return Collapsed(visible, edge, thicknessPx);
        var wa = monitor.WorkArea;
        return edge switch
        {
            ScreenEdge.Left => visible with { X = wa.X - visible.Width },
            ScreenEdge.Right => visible with { X = wa.X + wa.Width },
            ScreenEdge.Top => visible with { Y = wa.Y - visible.Height },
            _ => visible with { Y = wa.Y + wa.Height }
        };
    }

    /// <summary>True when a different monitor adjoins <paramref name="monitor"/> beyond <paramref name="edge"/> next to the window.</summary>
    internal static bool HasNeighbor(IReadOnlyList<MonitorInfo> all, MonitorInfo monitor, ScreenEdge edge, WidgetRect window)
    {
        var wa = monitor.WorkArea;
        const double slack = 2;
        foreach (var other in all)
        {
            if (other.Id == monitor.Id) continue;
            var o = other.WorkArea;
            var overlapsY = o.Y < window.Y + window.Height && o.Y + o.Height > window.Y;
            var overlapsX = o.X < window.X + window.Width && o.X + o.Width > window.X;
            var beyond = edge switch
            {
                ScreenEdge.Right => o.X >= wa.X + wa.Width - slack && overlapsY,
                ScreenEdge.Left => o.X + o.Width <= wa.X + slack && overlapsY,
                ScreenEdge.Top => o.Y + o.Height <= wa.Y + slack && overlapsX,
                _ => o.Y >= wa.Y + wa.Height - slack && overlapsX
            };
            if (beyond) return true;
        }
        return false;
    }

    /// <summary>Slides the window between two rectangles (position and size) with an ease-out curve.</summary>
    private void Animate(IntPtr hwnd, WidgetRect from, WidgetRect to, TimeSpan duration, Action done)
    {
        var version = ++_animationVersion;
        _animating = true;
        RelaxMinSize();
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            if (version != _animationVersion) { timer.Stop(); return; }
            var t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / duration.TotalMilliseconds);
            var e = 1 - Math.Pow(1 - t, 3); // cubic ease-out, same feel as the AIUsage Monitor widget
            Native.SetBoundsPx(hwnd, new WidgetRect(
                from.X + (to.X - from.X) * e, from.Y + (to.Y - from.Y) * e,
                from.Width + (to.Width - from.Width) * e, from.Height + (to.Height - from.Height) * e));
            if (t < 1) return;
            timer.Stop();
            _animating = false;
            done();               // hide / final bounds first, so restoring the minimum cannot flash a bigger window
            RestoreMinSize();
        };
        timer.Start();
    }

    // ---- window messages: move/resize gestures ----------------------------------------------------------------

    /// <summary>Test hook: feeds a window message through the real message handler.</summary>
    internal (bool Handled, IntPtr Result) DispatchMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        var handled = false;
        var result = WndProc(Native.Handle(_host), msg, wParam, lParam, ref handled);
        return (handled, result);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WmSysCommand:
            {
                var cmd = (int)(wParam.ToInt64() & 0xFFF0);
                _pending = cmd == Native.ScMove ? PendingKind.Move : cmd == Native.ScSize ? PendingKind.Resize : PendingKind.None;
                break;
            }
            case Native.WmEnterSizeMove:
                OnEnterSizeMove();
                break;

            case Native.WmMoving:
                if (OnMoving(lParam)) { handled = true; return new IntPtr(1); }
                break;

            case Native.WmExitSizeMove:
                OnExitSizeMove(hwnd);
                break;

            case Native.WmDpiChanged:
            case 0x007E: // WM_DISPLAYCHANGE
                _host.Dispatcher.BeginInvoke(new Action(Reapply), DispatcherPriority.Background);
                break;
        }
        return IntPtr.Zero;
    }

    private void OnEnterSizeMove()
    {
        _animationVersion++; // a running slide must not fight the user's drag
        _animating = false;
        RestoreMinSize();
        if (_pending == PendingKind.Move) Machine.BeginMove();
        else if (_pending == PendingKind.Resize) Machine.BeginResize();
        _pending = PendingKind.None;
    }

    private bool OnMoving(IntPtr lParam)
    {
        if (Machine.State is not (HostWindowState.Dragging or HostWindowState.SnapPreview)) return false;
        var r = Marshal.PtrToStructure<Native.RECT>(lParam);
        var rect = new WidgetRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var monitor = MonitorMath.Pick(_monitors.GetMonitors(), rect);
        var edge = Machine.Move(rect, monitor);
        if (edge == ScreenEdge.None || !MagneticPull) return false;

        var snapped = SnapMath.SnapTo(rect, monitor, edge);
        r.Left = (int)Math.Round(snapped.X);
        r.Top = (int)Math.Round(snapped.Y);
        r.Right = (int)Math.Round(snapped.X + snapped.Width);
        r.Bottom = (int)Math.Round(snapped.Y + snapped.Height);
        Marshal.StructureToPtr(r, lParam, false);
        return true;
    }

    private void OnExitSizeMove(IntPtr hwnd)
    {
        var bounds = Native.GetBoundsPx(hwnd);
        var monitor = MonitorMath.Pick(_monitors.GetMonitors(), bounds);
        HostPlacementResult? result = null;
        if (Machine.State is HostWindowState.Dragging or HostWindowState.SnapPreview)
            result = Machine.EndMove(bounds, monitor);
        else if (Machine.State == HostWindowState.Resizing)
            result = Machine.EndResize(bounds, monitor);
        if (result?.HostBounds is { } b)
        {
            _visibleBounds = b;
            if (b != bounds) Native.SetBoundsPx(hwnd, b);
            _hostSizeDip = new WidgetSize(b.Width / monitor.DpiScale, b.Height / monitor.DpiScale);
        }
        ApplyTopmost();
    }

    // ---- handle dragging ---------------------------------------------------------------------------------------------

    private MonitorInfo MonitorAt(Point p) =>
        MonitorMath.Pick(_monitors.GetMonitors(), new WidgetRect(p.X, p.Y, 1, 1));

    private void OnHandleDragStarted(Point p)
    {
        _handleDragStartBounds = Native.GetBoundsPx(Native.Handle(_handle));
        Machine.BeginHandleDrag();
    }

    private void OnHandleDragMoved(Point p)
    {
        var monitor = MonitorAt(p);
        var r = Machine.HandleDragMove((p.X, p.Y), monitor);
        var s = Machine.Settings;

        if (r.Edge != ScreenEdge.None)
        {
            _preview.ShowAt(SnapMath.HandleBounds(monitor, r.Edge, s.HandleThickness, HandleLengthDip, r.Ratio));
        }
        else
        {
            var size = new WidgetSize(_hostSizeDip.Width * monitor.DpiScale, _hostSizeDip.Height * monitor.DpiScale);
            _preview.ShowAt(SnapMath.ClampInside(
                new WidgetRect(p.X - size.Width / 2, p.Y - size.Height / 2, size.Width, size.Height), monitor.WorkArea));
        }

        // The strip follows the pointer while dragging.
        var hb = Native.GetBoundsPx(Native.Handle(_handle));
        Native.SetBoundsPx(Native.Handle(_handle), new WidgetRect(p.X - hb.Width / 2, p.Y - hb.Height / 2, hb.Width, hb.Height));
    }

    private void OnHandleDragEnded(Point p, bool commit)
    {
        _preview.Hide();
        if (Machine.State is not (HostWindowState.Dragging or HostWindowState.SnapPreview)) return;

        // A cancelled drag (capture lost) settles where the strip started.
        if (!commit)
            p = new Point(_handleDragStartBounds.X + _handleDragStartBounds.Width / 2,
                          _handleDragStartBounds.Y + _handleDragStartBounds.Height / 2);

        var monitor = MonitorAt(p);
        var sizePx = new WidgetSize(_hostSizeDip.Width * monitor.DpiScale, _hostSizeDip.Height * monitor.DpiScale);
        var result = Machine.EndHandleDrag((p.X, p.Y), monitor, sizePx);

        if (result.State == HostWindowState.SnappedHidden)
        {
            _handle.Place(HandleRect(_monitors.GetMonitors()), Machine.Settings.SnappedEdge);
        }
        else if (result.HostBounds is { } b)
        {
            _visibleBounds = b;
            Native.SetBoundsPx(Native.Handle(_host), b);
            _handle.Hide();
            if (!_host.IsVisible) _host.Show();
            ApplyTopmost();
        }
    }

    // ---- display / DPI changes ---------------------------------------------------------------------------------------

    /// <summary>
    /// Re-fits the Host after a monitor or DPI change: a snapped Host is re-placed on its edge with the
    /// new monitor's scale, a floating one is pulled back inside the visible work area.
    /// </summary>
    public void Reapply()
    {
        if (_disposed) return;
        var monitors = _monitors.GetMonitors();
        var hwnd = Native.Handle(_host);
        var s = Machine.Settings;

        if (Machine.Placement == HostPlacementState.Snapped)
        {
            var monitor = CurrentMonitor(monitors);
            var sizePx = new WidgetSize(_hostSizeDip.Width * monitor.DpiScale, _hostSizeDip.Height * monitor.DpiScale);
            _visibleBounds = SnapMath.PlaceAtEdge(sizePx, monitor, s.SnappedEdge, s.HandleOffsetRatio);
            if (_host.IsVisible && !_animating) Native.SetBoundsPx(hwnd, _visibleBounds);
            if (_handle.IsVisible) _handle.Place(HandleRect(monitors), s.SnappedEdge);
        }
        else if (_host.IsVisible && Machine.State == HostWindowState.Floating)
        {
            var b = Native.GetBoundsPx(hwnd);
            var monitor = MonitorMath.Pick(monitors, b);
            var fixedBounds = SnapMath.ClampInside(b, monitor.WorkArea);
            if (fixedBounds != b) Native.SetBoundsPx(hwnd, fixedBounds);
            _visibleBounds = fixedBounds;
        }
    }

    /// <summary>Call before exit so the latest placement and size are stored.</summary>
    public void Flush()
    {
        if (_host.IsVisible && !_animating && Machine.State is HostWindowState.Floating or HostWindowState.SnappedVisible)
            _visibleBounds = Native.GetBoundsPx(Native.Handle(_host));
        _persist?.Invoke(Machine.Settings with { HostSizeDip = _hostSizeDip });
    }
}
