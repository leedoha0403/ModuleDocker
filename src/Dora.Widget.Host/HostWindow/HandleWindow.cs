using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Dora.Widget.Abstractions;
using Dora.Widget.Runtime.HostWindow;

namespace Dora.Widget.Host.HostWindow;

/// <summary>
/// The thin strip that stays on screen while the Host is hidden. It offers a drag area, hover
/// detection, an active visual state, an optional icon and an optional attention indicator.
/// All coordinates handed in/out are physical pixels.
/// </summary>
public sealed class HandleWindow : Window
{
    private const double DragThresholdPx = 4;
    private static readonly Brush Ink = MakeInk();

    // Shared by every window on every UI thread, so it must be frozen.
    private static Brush MakeInk()
    {
        var b = new SolidColorBrush(Color.FromRgb(0x0B, 0x14, 0x18));
        b.Freeze();
        return b;
    }

    private readonly Border _surface;
    private readonly TextBlock _glyph;
    private readonly TextBlock _arrow;
    private readonly StackPanel _stack;
    private readonly Ellipse _dot;
    private bool _active;
    private bool _pressed;
    private bool _dragging;
    private Point _pressPx;

    public HandleWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Title = "ModuleDock handle";

        // Same tab as the AIUsage Monitor widget's handle: accent colour, an arrow that points out of the
        // edge ("hover here and it comes out this way") and a grip.
        _arrow = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _glyph = new TextBlock
        {
            Text = "⋮",
            FontSize = 13,
            Foreground = Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _stack.Children.Add(_arrow);
        _stack.Children.Add(_glyph);
        _dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = HostBrushes.Pinned,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        var grid = new Grid();
        grid.Children.Add(_stack);
        grid.Children.Add(_dot);

        _surface = new Border
        {
            Background = HostBrushes.Focus,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Child = grid,
            Cursor = Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.35 }
        };
        Opacity = 0.9;
        Content = _surface;

        SourceInitialized += (_, _) => Native.MakeOverlay(this, clickThrough: false);

        MouseEnter += (_, _) => { PointerEntered?.Invoke(); UpdateVisual(); };
        MouseLeave += (_, _) => { if (!_dragging) PointerLeft?.Invoke(); UpdateVisual(); };
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, _) => { if (_dragging) EndDrag(commit: false); _pressed = false; };
    }

    /// <summary>Pointer entered the strip (starts the reveal delay).</summary>
    public event Action? PointerEntered;
    public event Action? PointerLeft;

    /// <summary>The button went down on the strip (a drag or a click may follow): a pending hover reveal must not fire.</summary>
    public event Action? PressStarted;

    /// <summary>A press-release without dragging.</summary>
    public event Action? Clicked;

    /// <summary>Handle drag lifecycle; points are cursor positions in physical pixels.</summary>
    public event Action<Point>? DragStarted;
    public event Action<Point>? DragMoved;
    public event Action<Point, bool>? DragEnded;

    public bool IsDragging => _dragging;

    /// <summary>Optional icon glyph shown instead of the grip.</summary>
    public string? Glyph
    {
        get => _customGlyph;
        set
        {
            _customGlyph = value;
            if (!string.IsNullOrEmpty(value)) _glyph.Text = value;
        }
    }
    private string? _customGlyph;

    /// <summary>Optional unread/status indicator dot.</summary>
    public bool Indicator
    {
        get => _dot.Visibility == Visibility.Visible;
        set => _dot.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Visual active state (reveal pending / pointer over).</summary>
    public bool Active
    {
        get => _active;
        set { _active = value; UpdateVisual(); }
    }

    /// <summary>Positions the strip (physical pixels) and orients the grip for the edge.</summary>
    public void Place(WidgetRect boundsPx, ScreenEdge edge)
    {
        var vertical = edge.IsVertical();
        _stack.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        _arrow.Text = edge switch { ScreenEdge.Left => "›", ScreenEdge.Right => "‹", ScreenEdge.Top => "⌄", _ => "⌃" };
        _glyph.Margin = vertical ? new Thickness(0, 2, 0, 0) : new Thickness(4, 0, 0, 0);
        if (string.IsNullOrEmpty(_customGlyph)) _glyph.Text = vertical ? "⋮" : "⋯";
        Native.SetBoundsPx(Native.Handle(this), boundsPx);
        _surface.CornerRadius = edge switch
        {
            ScreenEdge.Right => new CornerRadius(8, 0, 0, 8),
            ScreenEdge.Left => new CornerRadius(0, 8, 8, 0),
            ScreenEdge.Top => new CornerRadius(0, 0, 8, 8),
            _ => new CornerRadius(8, 8, 0, 0)
        };
    }

    /// <summary>A few soft blinks right after the Host folds away so the eye finds the handle.</summary>
    public void Pulse()
    {
        var pulse = new DoubleAnimation(1.0, 0.35, new Duration(TimeSpan.FromMilliseconds(420)))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        pulse.Completed += (_, _) => UpdateVisual();
        BeginAnimation(OpacityProperty, pulse);
    }

    private void UpdateVisual()
    {
        BeginAnimation(OpacityProperty, null);
        var hot = _active || IsMouseOver || _dragging;
        Opacity = hot ? 1 : 0.9;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _dragging = false;
        _pressPx = Interop.CursorPixels();
        PressStarted?.Invoke();
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(commit: true); return; }
        var p = Interop.CursorPixels();
        if (!_dragging)
        {
            if ((p - _pressPx).Length < DragThresholdPx) return;
            _dragging = true;
            DragStarted?.Invoke(p);
        }
        DragMoved?.Invoke(p);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        if (_dragging) EndDrag(commit: true);
        else
        {
            _pressed = false;
            ReleaseMouseCapture();
            Clicked?.Invoke();
        }
    }

    private void EndDrag(bool commit)
    {
        var p = Interop.CursorPixels();
        var was = _dragging;
        _dragging = false;
        _pressed = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        UpdateVisual();
        if (was) DragEnded?.Invoke(p, commit);
    }

    /// <summary>Test hook: runs the drag lifecycle with synthetic pixel positions.</summary>
    internal void SimulateDrag(Point start, IEnumerable<Point> path, Point end, bool commit = true)
    {
        _dragging = true;
        DragStarted?.Invoke(start);
        foreach (var p in path) DragMoved?.Invoke(p);
        _dragging = false;
        DragEnded?.Invoke(end, commit);
    }

    internal void SimulateHover(bool enter)
    {
        if (enter) PointerEntered?.Invoke(); else PointerLeft?.Invoke();
    }

    internal void SimulateClick() => Clicked?.Invoke();

    internal void SimulatePress() => PressStarted?.Invoke();
}

/// <summary>Click-through overlay showing where the Host would snap to.</summary>
public sealed class SnapPreviewWindow : Window
{
    public SnapPreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x38, 0x4C, 0x9A, 0xFF)),
            BorderBrush = HostBrushes.Drop,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6)
        };
        SourceInitialized += (_, _) => Native.MakeOverlay(this, clickThrough: true);
    }

    public void ShowAt(WidgetRect boundsPx)
    {
        if (!IsVisible) Show();
        Native.SetBoundsPx(Native.Handle(this), boundsPx);
    }
}
