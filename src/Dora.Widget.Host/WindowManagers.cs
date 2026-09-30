using System.Windows;
using System.Windows.Controls;
using Dora.Widget.Abstractions;
using Dora.Widget.Host.HostWindow;
using Dora.Widget.Runtime;

namespace Dora.Widget.Host;

/// <summary>Borderless, topmost container for a floating widget. Owned and created by the Host only.</summary>
public sealed class FloatingWindow : Window
{
    public FloatingWindow(WidgetChrome chrome)
    {
        Chrome = chrome;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Manual;
        Background = HostBrushes.Background;
        Title = chrome.Instance.Manifest.Name;
        Content = chrome;
    }

    public WidgetChrome Chrome { get; }

    /// <summary>Set before closing programmatically so the close is not treated as a user re-dock.</summary>
    public bool ClosingProgrammatically { get; set; }
}

/// <summary>Creates floating windows, tracks their bounds and hands widgets back to the dock.</summary>
public sealed class FloatingWindowManager
{
    private readonly Dictionary<string, FloatingWindow> _windows = new();
    private readonly Action<string> _onUserClosed;
    private readonly Action<string, WidgetRect> _onBoundsChanged;

    public FloatingWindowManager(Action<string> onUserClosed, Action<string, WidgetRect> onBoundsChanged)
    {
        _onUserClosed = onUserClosed;
        _onBoundsChanged = onBoundsChanged;
    }

    public bool Contains(string instanceId) => _windows.ContainsKey(instanceId);

    public FloatingWindow? Get(string instanceId) => _windows.GetValueOrDefault(instanceId);

    /// <summary>
    /// Moves the chrome into a new floating window at <paramref name="boundsPx"/> (physical pixels, on
    /// whichever monitor that is). The window content keeps its natural size in DIPs.
    /// </summary>
    public FloatingWindow Show(WidgetChrome chrome, WidgetRect boundsPx)
    {
        var id = chrome.InstanceId;
        if (_windows.TryGetValue(id, out var existing)) return existing;

        var natural = chrome.Instance.Manifest.Layout.NaturalSize;
        chrome.RemoveFromParent();
        chrome.Width = natural.Width;
        chrome.Height = natural.Height;
        chrome.ApplyMode(WidgetDisplayMode.Natural);

        var window = new FloatingWindow(chrome)
        {
            Width = natural.Width,
            Height = natural.Height,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        var hwnd = Native.Handle(window);
        Native.SetBoundsPx(hwnd, boundsPx);   // before Show, so it never flashes at a default position
        window.LocationChanged += (_, _) => _onBoundsChanged(id, Native.GetBoundsPx(hwnd));
        window.Closing += (_, _) =>
        {
            if (window.ClosingProgrammatically) return;
            _windows.Remove(id);
            window.Content = null; // free the chrome so it can be re-docked
            _onUserClosed(id);
        };
        _windows[id] = window;
        window.Show();
        Native.SetBoundsPx(hwnd, boundsPx);   // WPF may have applied its own position/DPI size on Show
        return window;
    }

    /// <summary>Moves a floating window while it is dragged; position in physical pixels, size unchanged.</summary>
    public void MoveTo(string instanceId, double xPx, double yPx)
    {
        if (!_windows.TryGetValue(instanceId, out var w)) return;
        var hwnd = Native.Handle(w);
        var b = Native.GetBoundsPx(hwnd);
        Native.SetBoundsPx(hwnd, new WidgetRect(xPx, yPx, b.Width, b.Height));
    }

    /// <summary>Detaches the chrome from its floating window and closes the window.</summary>
    public void Release(string instanceId)
    {
        if (!_windows.Remove(instanceId, out var w)) return;
        w.Content = null;
        w.ClosingProgrammatically = true;
        w.Close();
    }

    public void CloseAll()
    {
        foreach (var w in _windows.Values.ToList())
        {
            w.ClosingProgrammatically = true;
            w.Content = null;
            w.Close();
        }
        _windows.Clear();
    }
}

/// <summary>Opens at most one detail window per widget instance.</summary>
public sealed class DetailWindowManager
{
    private readonly WidgetRuntime _runtime;
    private readonly WidgetStateMachine _machine;
    private readonly Dictionary<string, Window> _windows = new();

    public DetailWindowManager(WidgetRuntime runtime, WidgetStateMachine machine)
    {
        _runtime = runtime;
        _machine = machine;
    }

    public bool IsOpen(string instanceId) => _windows.ContainsKey(instanceId);

    public void Open(WidgetInstance instance)
    {
        var id = instance.State.InstanceId;
        if (_windows.TryGetValue(id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        object? view;
        try { view = _runtime.CreateDetailView(instance); }
        catch (Exception ex) { view = new TextBlock { Text = "Detail view failed: " + ex.Message, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap }; }
        if (view is null) return;

        var window = new Window
        {
            Title = instance.Manifest.Name,
            Width = 640,
            Height = 480,
            Background = HostBrushes.Background,
            Foreground = HostBrushes.Text,
            Content = view is UIElement ? view : new ContentPresenter { Content = view },
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.Closed += (_, _) =>
        {
            _windows.Remove(id);
            window.Content = null;
            if (_runtime.Find(id) != null) _machine.SetDetailOpen(id, false);
        };
        _windows[id] = window;
        _machine.SetDetailOpen(id, true);
        window.Show();
    }

    public void Close(string instanceId)
    {
        if (_windows.TryGetValue(instanceId, out var w)) w.Close();
    }

    public void CloseAll()
    {
        foreach (var w in _windows.Values.ToList()) w.Close();
    }
}

/// <summary>Semi-transparent, click-through preview shown while a docked widget is dragged out to detach.</summary>
public sealed class DetachGhostWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public DetachGhostWindow(string title, WidgetSize size)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = size.Width;
        Height = size.Height;
        Opacity = 0.6;
        Content = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
            BorderBrush = HostBrushes.Detach,
            Background = HostBrushes.Surface,
            Child = new TextBlock
            {
                Text = title,
                Foreground = HostBrushes.Text,
                Margin = new Thickness(10, 8, 10, 8),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExStyle,
                GetWindowLong(hwnd, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };
    }

    /// <summary>Positions the ghost in physical pixels with a size that matches the monitor under it.</summary>
    public void MoveToPx(WidgetRect boundsPx)
    {
        if (!IsVisible) Show();
        Native.SetBoundsPx(Native.Handle(this), boundsPx);
    }
}
