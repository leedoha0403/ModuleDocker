using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dora.Widget.Abstractions;
using Dora.Widget.Host.HostWindow;
using Dora.Widget.Runtime;
using Dora.Widget.Runtime.HostWindow;

namespace Dora.Widget.Host;

/// <summary>
/// Wires the runtime, the UI-agnostic <see cref="WidgetHostModel"/> and the WPF surfaces together.
/// All interaction changes go through <see cref="WidgetStateMachine"/>.
/// </summary>
public sealed class HostController
{
    private readonly WidgetRegistry _registry;
    private readonly WidgetRuntime _runtime;
    private readonly WidgetStateMachine _machine;
    private readonly WidgetHostModel _model;
    private readonly LayoutManager _layoutStore;
    private readonly DragManager _drag;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<string, WidgetChrome> _chromes = new();
    private readonly Action<string>? _log;
    private readonly IPermissionGrantStore _grants;
    private readonly IMonitorProvider _monitors;
    private readonly List<LayoutEntry> _orphans = new(); // entries of widgets that are not installed right now

    // hand-over to a widget's original application (see IWidgetDetachHandler)
    private readonly IWidgetStateStore _stateStore;
    private readonly Dictionary<string, IWidgetDetachHandler> _detachHandlers = new();
    private readonly HashSet<string> _detaching = new();
    private readonly CancellationTokenSource _handlerCts = new();
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    // drag session
    private WidgetChrome? _dragChrome;
    private Vector _grab;
    private DetachGhostWindow? _ghost;

    public HostController(
        WidgetRegistry registry,
        IWidgetStateStore stateStore,
        ILayoutStore layoutStore,
        HostOptions? options = null,
        Action<string>? log = null,
        IPermissionGrantStore? grants = null,
        IMonitorProvider? monitors = null,
        IEnumerable<IWidgetDetachHandler>? detachHandlers = null,
        string? announceEventName = null)
    {
        _log = log;
        _stateStore = stateStore;
        _monitors = monitors ?? new SystemMonitorProvider();
        _registry = registry;
        var opts = options ?? new HostOptions();
        _machine = new WidgetStateMachine(new DispatcherTimerScheduler(), opts);
        _grants = grants ?? new InMemoryPermissionGrantStore();
        _runtime = new WidgetRuntime(registry, _machine, stateStore,
            permissions: m => new WidgetPermissionService(
                m.Capabilities,
                _grants.Get(m.Id),
                prompt: caps => (PermissionPrompt ?? DefaultPermissionPrompt)(m, caps),
                onGranted: g => _grants.Set(m.Id, _grants.Get(m.Id) | g)),
            log: (level, msg, ex) => _log?.Invoke($"[{level}] {msg}{(ex is null ? "" : " - " + ex.Message)}"));
        _model = new WidgetHostModel(_machine, id => Manifest(id), opts);
        _layoutStore = new LayoutManager(layoutStore);
        _drag = new DragManager(opts);

        Panel = new WidgetHostPanel(_model);
        Floating = new FloatingWindowManager(OnFloatingClosedByUser, OnFloatingBoundsChanged);
        Details = new DetailWindowManager(_runtime, _machine);

        _machine.StateChanged += OnStateChanged;
        _announceEventName = announceEventName ?? WidgetAnnounce.EventName;
        RegisterDetachHandlers(detachHandlers);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveLayoutNow();
        };
    }

    public WidgetHostPanel Panel { get; }

    /// <summary>Open Host-owned popups; the Host window keeps itself visible while any is open.</summary>
    public HostPopupTracker Popups { get; } = new();

    /// <summary>Raised when a widget drag starts/ends and whether a drop preview (gap) is showing.</summary>
    public event Action<bool, bool>? DragActivityChanged;
    public FloatingWindowManager Floating { get; }
    public DetailWindowManager Details { get; }
    public WidgetRegistry Registry => _registry;
    public WidgetHostModel Model => _model;
    public WidgetRuntime Runtime => _runtime;

    /// <summary>Supplies Host window bounds for persistence.</summary>
    public Func<WidgetRect?>? HostBoundsProvider { get; set; }

    /// <summary>Asks the user whether a widget may use a declared capability; replaced in tests.</summary>
    public Func<WidgetManifest, WidgetCapabilities, Task<bool>>? PermissionPrompt { get; set; }

    /// <summary>When it returns true, declared capabilities are granted without asking (widgets the user installed).</summary>
    public Func<bool>? AutoGrantInstalledWidgets { get; set; }

    private Task<bool> DefaultPermissionPrompt(WidgetManifest manifest, WidgetCapabilities caps)
    {
        if (AutoGrantInstalledWidgets?.Invoke() == true)
        {
            _log?.Invoke($"[Info] granted {caps} to {manifest.Id} (installed widgets are trusted)");
            return Task.FromResult(true);
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.FromResult(false);
        return dispatcher.InvokeAsync(() =>
        {
            using (Popups.Scope())
            {
                var answer = MessageBox.Show(
                    Application.Current!.MainWindow,
                    $"\"{manifest.Name}\" 위젯이 다음 기능을 사용하려고 합니다: {caps}{Environment.NewLine}{Environment.NewLine}허용할까요?",
                    "ModuleDock 권한 요청", MessageBoxButton.YesNo, MessageBoxImage.Question);
                return answer == MessageBoxResult.Yes;
            }
        }).Task;
    }

    /// <summary>Tests drive gestures with synthetic cursor positions, where real mouse capture cannot be held.</summary>
    internal bool UseMouseCapture { get; set; } = true;

    internal bool GhostVisible => _ghost is { IsVisible: true };

    private WidgetManifest Manifest(string widgetId) =>
        _registry.TryGetManifest(widgetId, out var m) ? m : throw new KeyNotFoundException(widgetId);

    // ---- lifecycle ------------------------------------------------------------

    /// <summary>Restores the saved layout, or adds one instance of each widget on first run.</summary>
    public async Task RestoreAsync()
    {
        var snapshot = _layoutStore.Load();
        // A missing or empty layout means nothing to restore: start with one of each installed widget.
        if (snapshot is null || snapshot.Entries.Count == 0)
        {
            foreach (var m in _registry.Manifests.OrderBy(m => m.Name))
                await AddWidgetAsync(m.Id);
            return;
        }

        foreach (var e in snapshot.Entries)
        {
            if (!_registry.TryGetManifest(e.WidgetId, out _))
            {
                _orphans.Add(e); // keep it so reinstalling the widget brings its slot back
                continue;
            }
            try { await CreateAsync(e.WidgetId, e.InstanceId); }
            catch (WidgetRefusedException ex) { _log?.Invoke($"[Info] restore {e.WidgetId}: the widget declined ({ex.Message})"); }
            catch (Exception ex) { _log?.Invoke($"[Error] restore {e.WidgetId}: {ex.Message}"); }
        }

        _model.ApplySnapshot(snapshot);
        foreach (var (id, chrome) in _chromes.ToList())
        {
            var s = _machine.Get(id);
            if (s.DockState == DockState.Floating)
                ShowFloating(chrome, s.FloatingBounds is { } fb ? NormalizeFloating(chrome, fb) : DefaultFloatingBounds(chrome));
            else
                Panel.Add(chrome);
        }
        Panel.RequestRelayout();
    }

    public async Task AddWidgetAsync(string widgetId)
    {
        WidgetChrome chrome;
        try { chrome = await CreateAsync(widgetId, null); }
        catch (WidgetRefusedException ex)
        {
            _log?.Invoke($"[Info] {widgetId}: not added, the widget declined ({ex.Message})");
            return;
        }
        _model.AttachDocked(chrome.InstanceId);
        Panel.Add(chrome);
        ScheduleSave();
    }

    private async Task<WidgetChrome> CreateAsync(string widgetId, string? instanceId)
    {
        var instance = await _runtime.CreateInstanceAsync(widgetId, instanceId);
        object view;
        try { view = _runtime.GetSummaryView(instance); }
        catch (Exception ex)
        {
            _log?.Invoke($"[Error] {widgetId}: CreateSummaryView failed: {ex.Message}");
            view = new TextBlock
            {
                Text = instance.Manifest.Name + ": 불러오지 못했습니다",
                Foreground = HostBrushes.Detach,
                Margin = new Thickness(8),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        var chrome = new WidgetChrome(instance, view);
        Wire(chrome);
        _chromes[chrome.InstanceId] = chrome;
        return chrome;
    }

    public async Task RemoveWidgetAsync(string instanceId)
    {
        if (!_chromes.Remove(instanceId, out var chrome)) return;
        AbandonGestureOf(chrome);
        Details.Close(instanceId);
        Floating.Release(instanceId);
        Panel.Remove(chrome);
        chrome.RemoveFromParent();
        _model.Remove(instanceId);
        await _runtime.RemoveInstanceAsync(instanceId, deleteState: true);
        ScheduleSave();
    }

    public async Task ShutdownAsync()
    {
        _saveTimer.Stop();
        SaveLayoutNow();
        Details.CloseAll();
        Floating.CloseAll();
        _handlerCts.Cancel();
        _announceWait?.Unregister(null);
        _announce?.Dispose();
        foreach (var (signal, wait) in _openDetailWaits)
        {
            wait.Unregister(null);
            signal.Dispose();
        }
        _openDetailWaits.Clear();
        await _runtime.ShutdownAsync();
        foreach (var handler in _detachHandlers.Values)
        {
            try { await handler.ShutdownAsync(); }
            catch (Exception ex) { _log?.Invoke($"[Error] detach handler {handler.WidgetId} shutdown: {ex.Message}"); }
        }

        // Nothing may lay out or reference instances that no longer exist.
        foreach (var (id, chrome) in _chromes.ToList())
        {
            Panel.Remove(chrome);
            _model.Remove(id);
        }
        _chromes.Clear();
    }

    // ---- hand-over to the widget's original application ------------------------
    //
    // A widget may also exist as a separate program. Dragging it out of the Host then hands ownership to that
    // program (it gets the widget's state and the drop position) and the Host drops its own instance; dropping
    // the program's window onto the Host hands it back. One owner at a time; the side that acknowledges the
    // state owns the widget. Anything that goes wrong leaves the Host as the owner (floating is the fallback).

    private void RegisterDetachHandlers(IEnumerable<IWidgetDetachHandler>? handlers)
    {
        if (handlers is null) return;
        foreach (var handler in handlers)
        {
            if (!_detachHandlers.TryAdd(handler.WidgetId, handler))
            {
                _log?.Invoke($"[Warn] more than one hand-over handler for {handler.WidgetId}; ignoring {handler.GetType().Name}");
                continue;
            }
            var widgetId = handler.WidgetId;
            handler.DockRequested += request => _ui.InvokeAsync(() => DockFromExternalAsync(widgetId, request)).Task.Unwrap();
            handler.DockHover += point => _ui.BeginInvoke(new Action(() => ShowExternalDockHint(widgetId, point)));
            handler.DetachEnded += () => _log?.Invoke($"[Info] {widgetId}: the external application ended without docking");

            // Events are wired, so the handler may now connect to an application that was started on its own.
            _ = StartHandlerAsync(handler);
        }
        if (_detachHandlers.Count > 0) ListenForAnnouncements();
        foreach (var widgetId in _detachHandlers.Keys) ListenForOpenDetail(widgetId);
    }

    private readonly string _announceEventName;
    private EventWaitHandle? _announce;
    private RegisteredWaitHandle? _announceWait;

    // Applications signal WidgetAnnounce.EventName when they start; every handler then looks for its application once.
    private void ListenForAnnouncements()
    {
        try
        {
            _announce = new EventWaitHandle(false, EventResetMode.AutoReset, _announceEventName);
            _announceWait = ThreadPool.RegisterWaitForSingleObject(_announce, (_, timedOut) =>
            {
                if (timedOut || _handlerCts.IsCancellationRequested) return;
                foreach (var handler in _detachHandlers.Values.ToList()) _ = StartHandlerAsync(handler);
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) { _log?.Invoke($"[Warn] widget announcements unavailable: {ex.Message}"); }
    }

    private readonly List<(EventWaitHandle Event, RegisteredWaitHandle Wait)> _openDetailWaits = new();

    // An application whose widget the Host owns asks for the detail window here rather than opening its own main
    // screen: there is exactly one main screen per widget.
    private void ListenForOpenDetail(string widgetId)
    {
        try
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, WidgetAnnounce.OpenDetailEventName(widgetId, _announceEventName));
            var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, timedOut) =>
            {
                if (timedOut || _handlerCts.IsCancellationRequested) return;
                _ui.BeginInvoke(new Action(() => OpenDetailOfWidget(widgetId)));
            }, null, Timeout.Infinite, executeOnlyOnce: false);
            _openDetailWaits.Add((signal, wait));
        }
        catch (Exception ex) { _log?.Invoke($"[Warn] open-detail requests unavailable for {widgetId}: {ex.Message}"); }
    }

    private void OpenDetailOfWidget(string widgetId)
    {
        var id = _chromes.Values.FirstOrDefault(c => c.Instance.Manifest.Id == widgetId)?.InstanceId;
        if (id != null) OpenDetail(id);
    }

    private async Task StartHandlerAsync(IWidgetDetachHandler handler)
    {
        try { await handler.StartAsync(_handlerCts.Token); }
        catch (OperationCanceledException) { /* the Host is shutting down */ }
        catch (Exception ex) { _log?.Invoke($"[Error] detach handler {handler.WidgetId} could not start: {ex.Message}"); }
    }

    // Only widgets that declared ProcessExecution (the hand-over starts another program) and have a handler.
    private bool CanDetachExternally(WidgetChrome chrome) =>
        _detachHandlers.TryGetValue(chrome.Instance.Manifest.Id, out var handler) &&
        !handler.IsDetached &&
        (chrome.Instance.Manifest.Capabilities & WidgetCapabilities.ProcessExecution) != 0 &&
        !_detaching.Contains(chrome.InstanceId);

    private async Task DetachToExternalAsync(WidgetChrome chrome, WidgetRect bounds)
    {
        var id = chrome.InstanceId;
        if (!_detaching.Add(id)) return;
        var handedOver = false;
        try
        {
            var handler = _detachHandlers[chrome.Instance.Manifest.Id];
            // The hand-over starts another program on the user's behalf, so the widget needs the user's OK first.
            if (await chrome.Instance.Context.Permissions.RequestAsync(WidgetCapabilities.ProcessExecution))
            {
                await _runtime.SaveStateAsync(chrome.Instance);
                var stored = _stateStore.Load(id);
                if (stored is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var dpi = DpiAt(new Point(bounds.X, bounds.Y)) * 96;
                    handedOver = await handler.DetachAsync(
                        new DetachRequest(id, stored.StateVersion, stored.Json, bounds, dpi), timeout.Token);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Error] hand-over of {chrome.Instance.Manifest.Id} failed: {ex.Message}");
        }
        finally
        {
            _detaching.Remove(id);
        }

        if (handedOver)
        {
            _log?.Invoke($"[Info] {chrome.Instance.Manifest.Id} handed over to its application");
            await RemoveWidgetAsync(id);
            return;
        }

        // Not taken over: keep the widget, as a Host floating window like any other widget.
        if (_chromes.ContainsKey(id) && _machine.Get(id).DockState == DockState.Docked && _model.Float(id, bounds))
        {
            Panel.Release(chrome);
            ShowFloating(chrome, bounds);
            ScheduleSave();
        }
    }

    // The application's window was dropped on the Host: recreate the widget from its state, at the drop position.
    private string? _pendingDetail;

    private async Task<bool> DockFromExternalAsync(string widgetId, DockRequest request)
    {
        if (!_registry.TryGetManifest(widgetId, out var manifest)) return false;
        if (!manifest.AllowMultipleInstances && _runtime.Instances.Any(i => i.State.WidgetId == widgetId))
        {
            _log?.Invoke($"[Warn] {widgetId}: not docking, the Host already has an instance");
            return false;
        }

        var instanceId = Guid.NewGuid().ToString("D");
        try
        {
            _stateStore.Save(instanceId, new StoredWidgetState(request.StateVersion, request.StateJson));
            var chrome = await CreateAsync(widgetId, instanceId);
            var index = _model.InsertionIndexAt(Panel.ScreenYToCanvas(request.ScreenCursor.Y));
            _model.AttachDocked(instanceId, index);
            Panel.Add(chrome);
            Panel.HideIndicator();
            Panel.SetDockHint(false);
            Panel.RequestRelayout();
            DragActivityChanged?.Invoke(false, false);
            ScheduleSave();
            _log?.Invoke($"[Info] {widgetId} docked from its application");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Error] docking {widgetId} from its application failed: {ex.Message}");
            try { _stateStore.Delete(instanceId); } catch { /* best effort */ }
            return false;
        }
    }

    // The application's window is being dragged over the Host (or left it): show where it would land.
    private void ShowExternalDockHint(string widgetId, WidgetPoint? point)
    {
        if (point is null)
        {
            Panel.HideIndicator();
            Panel.SetDockHint(false);
            DragActivityChanged?.Invoke(false, false);
            return;
        }
        if (!_registry.TryGetManifest(widgetId, out var manifest)) return;
        var index = _model.InsertionIndexAt(Panel.ScreenYToCanvas(point.Value.Y));
        Panel.ShowGap(index, manifest.Layout.CompactSize.Height);
        Panel.SetDockHint(true);
        DragActivityChanged?.Invoke(true, true);
    }

    // ---- persistence ----------------------------------------------------------

    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveLayoutNow()
    {
        try { _layoutStore.Save(_machine.States, HostBoundsProvider?.Invoke(), _orphans); }
        catch (Exception ex) { _log?.Invoke($"[Error] layout save failed: {ex.Message}"); }
    }

    // ---- state -> view --------------------------------------------------------

    private void OnStateChanged(WidgetRuntimeState state)
    {
        if (!_chromes.TryGetValue(state.InstanceId, out var chrome)) return;
        chrome.Refresh();
        Panel.RequestRelayout();
        ScheduleSave();
    }

    private void OnFloatingClosedByUser(string instanceId)
    {
        // Closing a floating window returns the widget to the dock; it never destroys the widget.
        if (!_chromes.TryGetValue(instanceId, out var chrome)) return;
        _model.Redock(instanceId);
        Panel.Add(chrome);
    }

    private void OnFloatingBoundsChanged(string instanceId, WidgetRect bounds)
    {
        if (!_machine.States.Any(s => s.InstanceId == instanceId)) return;
        _machine.Get(instanceId).FloatingBounds = bounds;
        ScheduleSave();
    }

    // ---- commands -------------------------------------------------------------

    public void TogglePin(string instanceId)
    {
        var s = _machine.Get(instanceId);
        if (s.InteractionState == WidgetInteractionState.Pinned) _machine.Unpin(instanceId);
        else
        {
            _machine.Click(instanceId); // pin applies to the focused widget
            _machine.Pin(instanceId);
        }
    }

    public void OpenDetail(string instanceId)
    {
        if (!_chromes.TryGetValue(instanceId, out var chrome)) return;
        if (!chrome.Instance.Manifest.SupportsDetailView) return;
        Details.Open(chrome.Instance);
    }

    public void ToggleFloat(string instanceId)
    {
        if (!_chromes.TryGetValue(instanceId, out var chrome)) return;
        var s = _machine.Get(instanceId);
        if (s.DockState == DockState.Docked)
        {
            var bounds = DefaultFloatingBounds(chrome);
            if (CanDetachExternally(chrome))
            {
                _ = DetachToExternalAsync(chrome, bounds);
                return;
            }
            if (_model.Float(instanceId, bounds))
            {
                Panel.Release(chrome);
                ShowFloating(chrome, bounds);
            }
        }
        else
        {
            Floating.Release(instanceId);
            _model.Redock(instanceId);
            Panel.Add(chrome);
        }
    }

    /// <summary>DPI scale of the monitor under a physical-pixel point.</summary>
    private double DpiAt(Point px) =>
        MonitorMath.Pick(_monitors.GetMonitors(), new WidgetRect(px.X, px.Y, 1, 1)).DpiScale;

    /// <summary>Just right of the Host window (physical pixels), staggered per floating widget.</summary>
    private WidgetRect DefaultFloatingBounds(WidgetChrome chrome)
    {
        var natural = chrome.Instance.Manifest.Layout.NaturalSize;
        var host = Panel.IsLoaded ? Interop.ScreenBoundsPx(Panel) : new Rect(100, 100, 0, 0);
        var dpi = Panel.IsLoaded ? Interop.Dpi(Panel) : 1.0;
        return NormalizeFloating(chrome, new WidgetRect(
            host.Right + 12 * dpi, host.Top + (12 + _chromes.Count(c => Floating.Contains(c.Key)) * 24) * dpi,
            natural.Width * dpi, natural.Height * dpi));
    }

    /// <summary>
    /// Fits saved floating bounds to the current monitors: natural size at that monitor's DPI, fully
    /// inside its work area (a saved position may belong to a monitor that is no longer there).
    /// </summary>
    private WidgetRect NormalizeFloating(WidgetChrome chrome, WidgetRect saved)
    {
        var natural = chrome.Instance.Manifest.Layout.NaturalSize;
        var monitor = MonitorMath.Pick(_monitors.GetMonitors(), saved);
        return SnapMath.ClampInside(
            new WidgetRect(saved.X, saved.Y, natural.Width * monitor.DpiScale, natural.Height * monitor.DpiScale),
            monitor.WorkArea);
    }

    private void ShowFloating(WidgetChrome chrome, WidgetRect bounds)
    {
        Floating.Show(chrome, bounds);
        chrome.Refresh();
    }

    // ---- pointer wiring -------------------------------------------------------

    private void Wire(WidgetChrome chrome)
    {
        var id = chrome.InstanceId;
        // A removed widget still gets a late MouseLeave from WPF's deferred mouse-over update; it is no longer known.
        chrome.MouseEnter += (_, _) => { if (_chromes.ContainsKey(id)) _machine.PointerEnter(id); };
        chrome.MouseLeave += (_, _) => { if (_dragChrome != chrome && _chromes.ContainsKey(id)) _machine.PointerLeave(id); };
        chrome.PreviewMouseLeftButtonDown += OnChromeDown;
        chrome.PreviewMouseMove += OnChromeMove;
        chrome.PreviewMouseLeftButtonUp += OnChromeUp;
        chrome.LostMouseCapture += (_, _) => { if (_dragChrome == chrome) FinishDrag(chrome, Interop.CursorPixels(), commit: false); };
        chrome.PinToggled += c => TogglePin(c.InstanceId);
        chrome.ContextMenuOpening += (_, e) =>
        {
            e.Handled = true;
            chrome.ContextMenu = BuildMenu(chrome);
            chrome.ContextMenu.IsOpen = true;
        };
    }

    private ContextMenu BuildMenu(WidgetChrome chrome)
    {
        var id = chrome.InstanceId;
        var s = _machine.Get(id);
        var m = chrome.Instance.Manifest;
        var menu = new ContextMenu();
        Popups.Track(menu);

        menu.Items.Add(Item(s.InteractionState == WidgetInteractionState.Pinned ? "고정 해제" : "고정", true, () => TogglePin(id)));
        menu.Items.Add(Item("상세 보기", m.SupportsDetailView, () => OpenDetail(id)));
        menu.Items.Add(Item(s.DockState == DockState.Docked ? "분리 (플로팅)" : "도킹", m.SupportsFloating || s.DockState == DockState.Floating,
            () => ToggleFloat(id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("삭제", true, () => _ = RemoveWidgetAsync(id)));
        return menu;

        static MenuItem Item(string header, bool enabled, Action click)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => click();
            return mi;
        }
    }

    private void OnChromeDown(object sender, MouseButtonEventArgs e)
    {
        var chrome = (WidgetChrome)sender;
        PressDown(chrome, Interop.CursorPixels(), e.ClickCount);
    }

    private void OnChromeMove(object sender, MouseEventArgs e)
    {
        var chrome = (WidgetChrome)sender;
        PressMove(chrome, Interop.CursorPixels(), e.LeftButton == MouseButtonState.Pressed);
    }

    private void OnChromeUp(object sender, MouseButtonEventArgs e)
    {
        var chrome = (WidgetChrome)sender;
        PressUp(chrome, Interop.CursorPixels());
    }

    // The Press* methods hold the gesture logic; cursor positions are physical screen pixels, so widgets on
    // monitors with different DPI compare correctly.
    // They are internal so tests can drive complete gestures without a real mouse.

    internal void PressDown(WidgetChrome chrome, Point cursor, int clickCount)
    {
        var id = chrome.InstanceId;
        if (!_chromes.ContainsKey(id)) return; // removed while events were still in flight
        _machine.Click(id);

        // A double-click opens the detail on release, and only when the press did not turn into a drag: a quick
        // second press of a drag back and forth (a widget that just changed owner) must not open a window.
        _pendingDetail = clickCount >= 2 ? id : null;

        var docked = _machine.Get(id).DockState == DockState.Docked;
        _drag.PointerDown(id, cursor.X, cursor.Y, docked);
    }

    internal void PressMove(WidgetChrome chrome, Point cursor, bool leftPressed)
    {
        if (!_chromes.ContainsKey(chrome.InstanceId)) return; // removed while events were still in flight
        if (_drag.InstanceId != chrome.InstanceId) return;

        if (!leftPressed)
        {
            if (_dragChrome == chrome) FinishDrag(chrome, cursor, commit: true);
            else _drag.Cancel();
            return;
        }

        if (_drag.Phase == DragPhase.Pending)
        {
            if (_drag.PointerMove(cursor.X, cursor.Y, Interop.Dpi(chrome))) StartDrag(chrome, cursor);
        }
        else if (_drag.Phase == DragPhase.Dragging && _dragChrome == chrome)
        {
            UpdateDrag(chrome, cursor);
        }
    }

    internal void PressUp(WidgetChrome chrome, Point cursor)
    {
        var id = chrome.InstanceId;
        var openDetail = _pendingDetail == id && _dragChrome != chrome && _drag.Phase == DragPhase.Pending;
        _pendingDetail = null;
        if (_dragChrome == chrome) FinishDrag(chrome, cursor, commit: true);
        else if (_drag.InstanceId == id) _drag.Cancel();
        if (openDetail) OpenDetail(id);
    }

    // Ends any drag in progress on a chrome that is going away, so later pointer events find no half-finished gesture.
    private void AbandonGestureOf(WidgetChrome chrome)
    {
        if (_dragChrome == chrome)
        {
            _dragChrome = null;
            HideGhost();
            Mouse.OverrideCursor = null;
            DragActivityChanged?.Invoke(false, false);
            if (chrome.IsMouseCaptured) chrome.ReleaseMouseCapture();
        }
        if (_drag.InstanceId == chrome.InstanceId) _drag.Cancel();
    }

    private void StartDrag(WidgetChrome chrome, Point cursor)
    {
        var id = chrome.InstanceId;
        _dragChrome = chrome;
        var topLeft = Interop.ScreenBoundsPx(chrome).TopLeft;
        _grab = cursor - topLeft;

        _machine.DragStart(id);
        if (_machine.Get(id).DockState == DockState.Docked) Panel.BeginDrag(id);
        // Apply any pending focus/pin size change now so the drag gap matches what the user sees.
        if (_machine.Get(id).DockState == DockState.Docked) Panel.Relayout();
        DragActivityChanged?.Invoke(true, false);
        if (UseMouseCapture) chrome.CaptureMouse();
    }

    private void UpdateDrag(WidgetChrome chrome, Point cursor)
    {
        var id = chrome.InstanceId;
        var s = _machine.Get(id);
        var hostRect = Interop.ScreenBoundsPx(Panel);
        var panelDpi = Interop.Dpi(Panel);
        var docked = s.DockState == DockState.Docked;
        var margin = docked ? _model.Options.DetachMargin * panelDpi : 0;
        var inside = DockManager.IsInsideHostZone(
            new WidgetRect(hostRect.X, hostRect.Y, hostRect.Width, hostRect.Height), cursor.X, cursor.Y, margin);

        var pointerCanvasY = Panel.ScreenYToCanvas(cursor.Y);
        var index = _model.InsertionIndexAt(pointerCanvasY);
        _drag.Evaluate(inside, index);
        DragActivityChanged?.Invoke(true, _drag.Target is DragTarget.Reorder or DragTarget.Dock);

        if (docked)
        {
            Panel.MoveDragged(id, pointerCanvasY - _grab.Y / panelDpi);
            if (_drag.Target == DragTarget.Reorder)
            {
                HideGhost();
                Mouse.OverrideCursor = null;
                Panel.ShowGap(index, Panel.Chromes.TryGetValue(id, out var self) ? self.Height : 0);
            }
            else
            {
                Panel.HideIndicator();
                var canFloat = _model.CanFloat(id);
                Panel.SetDetachHint(canFloat);
                Panel.SetBlockedHint(!canFloat);
                Mouse.OverrideCursor = canFloat ? null : Cursors.No;
                if (canFloat) ShowGhost(chrome, cursor);
                else HideGhost();
            }
        }
        else
        {
            Floating.MoveTo(id, cursor.X - _grab.X, cursor.Y - _grab.Y);
            if (_drag.Target == DragTarget.Dock)
            {
                Panel.ShowGap(index, chrome.Instance.Manifest.Layout.CompactSize.Height);
                Panel.SetDockHint(true);
            }
            else
            {
                Panel.HideIndicator();
                Panel.SetDockHint(false);
            }
        }
    }

    private void ShowGhost(WidgetChrome chrome, Point cursor)
    {
        var natural = chrome.Instance.Manifest.Layout.NaturalSize;
        _ghost ??= new DetachGhostWindow(chrome.Instance.Manifest.Name, natural, chrome);
        var dpi = DpiAt(cursor);
        _ghost.MoveToPx(new WidgetRect(cursor.X - _grab.X, cursor.Y - _grab.Y, natural.Width * dpi, natural.Height * dpi));
    }

    private void HideGhost()
    {
        _ghost?.Close();
        _ghost = null;
    }

    private void FinishDrag(WidgetChrome chrome, Point cursor, bool commit)
    {
        var id = chrome.InstanceId;
        var wasDrag = _drag.PointerUp();
        var target = _drag.Target;
        var index = _drag.InsertionIndex;
        var grab = _grab;
        _dragChrome = null;
        HideGhost();
        Mouse.OverrideCursor = null;
        DragActivityChanged?.Invoke(false, false);
        if (chrome.IsMouseCaptured) chrome.ReleaseMouseCapture();

        if (!wasDrag || _machine.Get(id).InteractionState != WidgetInteractionState.Dragging)
        {
            Panel.EndDrag();
            return;
        }

        var s = _machine.Get(id);
        var dockBefore = s.DockState;
        if (!commit)
        {
            _machine.DragEnd(id, DragEndKind.Restore);
        }
        else if (s.DockState == DockState.Docked)
        {
            if (target == DragTarget.Reorder)
                _model.DropReorder(id, index);
            else
            {
                var natural = chrome.Instance.Manifest.Layout.NaturalSize;
                var dpi = DpiAt(cursor);
                var bounds = new WidgetRect(cursor.X - grab.X, cursor.Y - grab.Y, natural.Width * dpi, natural.Height * dpi);
                if (CanDetachExternally(chrome))
                {
                    // Stays docked until the widget's own application has taken over; floating is the fallback.
                    _machine.DragEnd(id, DragEndKind.Restore);
                    _ = DetachToExternalAsync(chrome, bounds);
                }
                else if (_model.DropDetach(id, bounds))
                {
                    Panel.Release(chrome);
                    ShowFloating(chrome, bounds);
                }
            }
        }
        else
        {
            if (target == DragTarget.Dock)
            {
                Floating.Release(id);
                _model.DropDock(id, index);
                Panel.Add(chrome);
            }
            else
            {
                _machine.DragEnd(id, DragEndKind.Restore);
            }
        }

        Panel.EndDrag();

        // The pointer may have ended over (or away from) the widget while capture was active.
        // After a dock/detach the chrome sits under the pointer in a new container, so the drop
        // result (Focused) stands; only re-sync pointer state when the widget stayed where it was.
        if (_chromes.ContainsKey(id) && s.DockState == dockBefore)
        {
            if (chrome.IsMouseOver) _machine.PointerEnter(id);
            else _machine.PointerLeave(id);
        }
        ScheduleSave();
    }
}
