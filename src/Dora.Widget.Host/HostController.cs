using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dora.Widget.Abstractions;
using Dora.Widget.Runtime;

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
    private readonly List<LayoutEntry> _orphans = new(); // entries of widgets that are not installed right now

    // drag session
    private WidgetChrome? _dragChrome;
    private Vector _grab;
    private DetachGhostWindow? _ghost;

    public HostController(
        WidgetRegistry registry,
        IWidgetStateStore stateStore,
        ILayoutStore layoutStore,
        HostOptions? options = null,
        Action<string>? log = null)
    {
        _log = log;
        _registry = registry;
        var opts = options ?? new HostOptions();
        _machine = new WidgetStateMachine(new DispatcherTimerScheduler(), opts);
        _runtime = new WidgetRuntime(registry, _machine, stateStore,
            log: (level, msg, ex) => _log?.Invoke($"[{level}] {msg}{(ex is null ? "" : " - " + ex.Message)}"));
        _model = new WidgetHostModel(_machine, id => Manifest(id), opts);
        _layoutStore = new LayoutManager(layoutStore);
        _drag = new DragManager(opts);

        Panel = new WidgetHostPanel(_model);
        Floating = new FloatingWindowManager(OnFloatingClosedByUser, OnFloatingBoundsChanged);
        Details = new DetailWindowManager(_runtime, _machine);

        _machine.StateChanged += OnStateChanged;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveLayoutNow();
        };
    }

    public WidgetHostPanel Panel { get; }
    public FloatingWindowManager Floating { get; }
    public DetailWindowManager Details { get; }
    public WidgetRegistry Registry => _registry;
    public WidgetHostModel Model => _model;
    public WidgetRuntime Runtime => _runtime;

    /// <summary>Supplies Host window bounds for persistence.</summary>
    public Func<WidgetRect?>? HostBoundsProvider { get; set; }

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
            catch (Exception ex) { _log?.Invoke($"[Error] restore {e.WidgetId}: {ex.Message}"); }
        }

        _model.ApplySnapshot(snapshot);
        foreach (var (id, chrome) in _chromes.ToList())
        {
            var s = _machine.Get(id);
            if (s.DockState == DockState.Floating)
                ShowFloating(chrome, s.FloatingBounds ?? DefaultFloatingBounds(chrome));
            else
                Panel.Add(chrome);
        }
        Panel.RequestRelayout();
    }

    public async Task AddWidgetAsync(string widgetId)
    {
        var chrome = await CreateAsync(widgetId, null);
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
                Text = instance.Manifest.Name + ": failed to load",
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
        await _runtime.ShutdownAsync();

        // Nothing may lay out or reference instances that no longer exist.
        foreach (var (id, chrome) in _chromes.ToList())
        {
            Panel.Remove(chrome);
            _model.Remove(id);
        }
        _chromes.Clear();
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

    private WidgetRect DefaultFloatingBounds(WidgetChrome chrome)
    {
        var natural = chrome.Instance.Manifest.Layout.NaturalSize;
        var host = Panel.IsLoaded ? Interop.ScreenBounds(Panel) : new Rect(100, 100, 0, 0);
        return new WidgetRect(host.Right + 12, host.Top + 12 + _chromes.Count(c => Floating.Contains(c.Key)) * 24,
            natural.Width, natural.Height);
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
        chrome.MouseEnter += (_, _) => _machine.PointerEnter(id);
        chrome.MouseLeave += (_, _) => { if (_dragChrome != chrome) _machine.PointerLeave(id); };
        chrome.PreviewMouseLeftButtonDown += OnChromeDown;
        chrome.PreviewMouseMove += OnChromeMove;
        chrome.PreviewMouseLeftButtonUp += OnChromeUp;
        chrome.LostMouseCapture += (_, _) => { if (_dragChrome == chrome) FinishDrag(chrome, Interop.CursorDips(chrome), commit: false); };
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

        menu.Items.Add(Item(s.InteractionState == WidgetInteractionState.Pinned ? "Unpin" : "Pin", true, () => TogglePin(id)));
        menu.Items.Add(Item("Open details", m.SupportsDetailView, () => OpenDetail(id)));
        menu.Items.Add(Item(s.DockState == DockState.Docked ? "Detach (float)" : "Dock", m.SupportsFloating || s.DockState == DockState.Floating,
            () => ToggleFloat(id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove", true, () => _ = RemoveWidgetAsync(id)));
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
        PressDown(chrome, Interop.CursorDips(chrome), e.ClickCount);
    }

    private void OnChromeMove(object sender, MouseEventArgs e)
    {
        var chrome = (WidgetChrome)sender;
        PressMove(chrome, Interop.CursorDips(chrome), e.LeftButton == MouseButtonState.Pressed);
    }

    private void OnChromeUp(object sender, MouseButtonEventArgs e)
    {
        var chrome = (WidgetChrome)sender;
        PressUp(chrome, Interop.CursorDips(chrome));
    }

    // The Press* methods hold the gesture logic; cursor positions are screen DIPs (see Interop.CursorDips).
    // They are internal so tests can drive complete gestures without a real mouse.

    internal void PressDown(WidgetChrome chrome, Point cursor, int clickCount)
    {
        var id = chrome.InstanceId;
        _machine.Click(id);

        if (clickCount >= 2)
        {
            _drag.Cancel();
            OpenDetail(id);
            return;
        }

        var docked = _machine.Get(id).DockState == DockState.Docked;
        _drag.PointerDown(id, cursor.X, cursor.Y, docked);
    }

    internal void PressMove(WidgetChrome chrome, Point cursor, bool leftPressed)
    {
        if (_drag.InstanceId != chrome.InstanceId) return;

        if (!leftPressed)
        {
            if (_dragChrome == chrome) FinishDrag(chrome, cursor, commit: true);
            else _drag.Cancel();
            return;
        }

        if (_drag.Phase == DragPhase.Pending)
        {
            if (_drag.PointerMove(cursor.X, cursor.Y)) StartDrag(chrome, cursor);
        }
        else if (_drag.Phase == DragPhase.Dragging && _dragChrome == chrome)
        {
            UpdateDrag(chrome, cursor);
        }
    }

    internal void PressUp(WidgetChrome chrome, Point cursor)
    {
        if (_dragChrome == chrome) FinishDrag(chrome, cursor, commit: true);
        else if (_drag.InstanceId == chrome.InstanceId) _drag.Cancel();
    }

    private void StartDrag(WidgetChrome chrome, Point cursor)
    {
        var id = chrome.InstanceId;
        _dragChrome = chrome;
        var topLeft = Interop.ScreenBounds(chrome).TopLeft;
        _grab = cursor - topLeft;

        _machine.DragStart(id);
        if (_machine.Get(id).DockState == DockState.Docked) Panel.BeginDrag(id);
        // Apply any pending focus/pin size change now so the drag gap matches what the user sees.
        if (_machine.Get(id).DockState == DockState.Docked) Panel.Relayout();
        if (UseMouseCapture) chrome.CaptureMouse();
    }

    private void UpdateDrag(WidgetChrome chrome, Point cursor)
    {
        var id = chrome.InstanceId;
        var s = _machine.Get(id);
        var hostRect = Interop.ScreenBounds(Panel);
        var docked = s.DockState == DockState.Docked;
        var margin = docked ? _model.Options.DetachMargin : 0;
        var inside = DockManager.IsInsideHostZone(
            new WidgetRect(hostRect.X, hostRect.Y, hostRect.Width, hostRect.Height), cursor.X, cursor.Y, margin);

        var pointerCanvasY = Panel.ScreenYToCanvas(cursor.Y);
        var index = _model.InsertionIndexAt(pointerCanvasY);
        _drag.Evaluate(inside, index);

        if (docked)
        {
            Panel.MoveDragged(id, pointerCanvasY - _grab.Y);
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
            var window = Floating.Get(id);
            if (window != null)
            {
                window.Left = cursor.X - _grab.X;
                window.Top = cursor.Y - _grab.Y;
            }
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
        _ghost ??= new DetachGhostWindow(chrome.Instance.Manifest.Name, natural);
        _ghost.MoveTo(cursor.X - _grab.X, cursor.Y - _grab.Y);
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
                var bounds = new WidgetRect(cursor.X - grab.X, cursor.Y - grab.Y, natural.Width, natural.Height);
                if (_model.DropDetach(id, bounds))
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
