using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Shapes;
using Dora.Widget.Abstractions;
using Dora.Widget.Runtime;

namespace Dora.Widget.Host;

/// <summary>The docked area: a scrollable canvas of widget chromes positioned by the adaptive layout.</summary>
public sealed class WidgetHostPanel : Grid
{
    /// <summary>Space kept free on the right for the scrollbar so layout never oscillates.</summary>
    private const double ScrollBarReserve = 10;

    private readonly WidgetHostModel _model;
    private readonly ScrollViewer _scroll;
    private readonly Canvas _canvas;
    private readonly Rectangle _indicator;
    private readonly Border _frame;
    private readonly Border _blockedBadge;
    private readonly Dictionary<string, WidgetChrome> _chromes = new();
    private bool _relayoutQueued;
    private string? _draggingId;
    private int _gapIndex = -1;      // insertion slot (0..N) in pre-removal coordinates, -1 = none
    private double _gapHeight;

    public WidgetHostPanel(WidgetHostModel model)
    {
        _model = model;
        Background = HostBrushes.Background;
        ClipToBounds = true;

        // Top/Left alignment is essential: with Stretch, WPF would centre a canvas that has an explicit
        // (small) Height inside the viewport, which breaks screen -> canvas coordinate mapping.
        _canvas = new Canvas
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _canvas,
            Background = Brushes.Transparent
        };
        _indicator = new Rectangle
        {
            Height = 3,
            RadiusX = 6,
            RadiusY = 6,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0x9A, 0xFF)),
            Stroke = HostBrushes.Drop,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _canvas.Children.Add(_indicator);
        Canvas.SetZIndex(_indicator, -1); // the gap marker must never cover a widget, least of all the dragged one

        _frame = new Border
        {
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            IsHitTestVisible = false
        };

        _blockedBadge = new Border
        {
            Background = HostBrushes.Pinned,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "This widget can’t be detached",
                Foreground = Brushes.Black,
                FontSize = 11
            }
        };

        Children.Add(_scroll);
        Children.Add(_frame);
        Children.Add(_blockedBadge);

        // Click on empty Host area releases normal focus (never unpins).
        _scroll.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!IsOverChrome(e.OriginalSource) && !IsOverScrollBar(e.OriginalSource))
                _model.Machine.ClickEmptyArea();
        };

        SizeChanged += (_, _) => Relayout();
    }

    /// <summary>Smooth slide when widgets change position. Tests turn it off to read final positions.</summary>
    internal bool AnimationsEnabled { get; set; } = true;

    public ScrollViewer Scroll => _scroll;

    public IReadOnlyDictionary<string, WidgetChrome> Chromes => _chromes;

    public void Add(WidgetChrome chrome)
    {
        chrome.RemoveFromParent();
        // A chrome that is re-docked still carries its old Canvas.Top; forget it so the widget
        // appears at its slot instead of sliding in from where it used to be.
        chrome.BeginAnimation(Canvas.TopProperty, null);
        chrome.ClearValue(Canvas.TopProperty);
        _chromes[chrome.InstanceId] = chrome;
        _canvas.Children.Add(chrome);
        RequestRelayout();
    }

    public void Remove(WidgetChrome chrome)
    {
        _chromes.Remove(chrome.InstanceId);
        _canvas.Children.Remove(chrome);
        RequestRelayout();
    }

    /// <summary>Removes the chrome from the canvas without forgetting the instance (moving to a floating window).</summary>
    public void Release(WidgetChrome chrome)
    {
        _chromes.Remove(chrome.InstanceId);
        _canvas.Children.Remove(chrome);
        RequestRelayout();
    }

    public void RequestRelayout()
    {
        if (_relayoutQueued) return;
        _relayoutQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _relayoutQueued = false;
            Relayout();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    public void Relayout()
    {
        var width = Math.Max(0, ActualWidth - ScrollBarReserve);
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var result = _model.Layout(new WidgetSize(width, height));
        var tops = ComputeTops(result);
        foreach (var slot in result.Slots)
        {
            if (!_chromes.TryGetValue(slot.InstanceId, out var chrome)) continue;
            chrome.Width = slot.Width;
            chrome.Height = slot.Height;
            Canvas.SetLeft(chrome, 0);
            if (slot.InstanceId != _draggingId) SlideTo(chrome, tops[slot.InstanceId]);
            chrome.ApplyMode(slot.Mode);
            chrome.Refresh();
        }

        foreach (var (id, chrome) in _chromes)
            if (result.Slots.All(s => s.InstanceId != id)) chrome.Refresh();

        _canvas.Width = width;
        _canvas.Height = Math.Max(result.TotalHeight + (_gapIndex >= 0 ? _gapHeight + _model.Options.ItemSpacing : 0), 0);
        PositionPlaceholder(result, tops);
    }

    /// <summary>
    /// Top of every slot. While a drag preview is active the other widgets are pushed apart so a
    /// gap of the dragged widget's height opens at the insertion slot.
    /// </summary>
    private Dictionary<string, double> ComputeTops(LayoutResult result)
    {
        var tops = new Dictionary<string, double>();
        if (_gapIndex < 0)
        {
            foreach (var s in result.Slots) tops[s.InstanceId] = s.Top;
            return tops;
        }

        var spacing = _model.Options.ItemSpacing;
        var others = result.Slots.Where(s => s.InstanceId != _draggingId).ToList();
        var target = TargetIndexAmongOthers(result);
        var y = 0d;
        for (var i = 0; i < others.Count; i++)
        {
            if (i == target) y += _gapHeight + spacing;
            tops[others[i].InstanceId] = y;
            y += others[i].Height + spacing;
        }
        if (_draggingId != null)
            foreach (var s in result.Slots.Where(s => s.InstanceId == _draggingId)) tops[s.InstanceId] = s.Top;
        return tops;
    }

    /// <summary>Converts the pre-removal insertion slot into an index in the list without the dragged widget.</summary>
    private int TargetIndexAmongOthers(LayoutResult result)
    {
        var from = _draggingId == null ? -1 : result.Slots.ToList().FindIndex(s => s.InstanceId == _draggingId);
        return from >= 0 && _gapIndex > from ? _gapIndex - 1 : _gapIndex;
    }

    private void PositionPlaceholder(LayoutResult result, Dictionary<string, double> tops)
    {
        if (_gapIndex < 0)
        {
            _indicator.Visibility = Visibility.Collapsed;
            return;
        }
        var others = result.Slots.Where(s => s.InstanceId != _draggingId).ToList();
        var target = Math.Min(TargetIndexAmongOthers(result), others.Count);
        var y = target < others.Count
            ? tops[others[target].InstanceId] - _gapHeight - _model.Options.ItemSpacing
            : (others.Count == 0 ? 0 : tops[others[^1].InstanceId] + others[^1].Height + _model.Options.ItemSpacing);
        _indicator.Width = _canvas.Width;
        _indicator.Height = _gapHeight;
        Canvas.SetTop(_indicator, Math.Max(0, y));
        _indicator.Visibility = Visibility.Visible;
    }

    /// <summary>Opens a gap of <paramref name="height"/> at the insertion slot and slides the others aside.</summary>
    public void ShowGap(int insertionIndex, double height)
    {
        if (_gapIndex == insertionIndex && Math.Abs(_gapHeight - height) < 0.5) return;
        _gapIndex = insertionIndex;
        _gapHeight = height;
        Reposition();
        SetDetachHint(false);
        SetBlockedHint(false);
    }

    /// <summary>Closes the gap; the others slide back.</summary>
    public void HideIndicator()
    {
        if (_gapIndex < 0) return;
        _gapIndex = -1;
        Reposition();
    }

    /// <summary>Re-applies positions from the last layout without recomputing modes.</summary>
    private void Reposition()
    {
        var result = _model.LastLayout;
        if (result is null) return;
        var tops = ComputeTops(result);
        foreach (var slot in result.Slots)
            if (slot.InstanceId != _draggingId && _chromes.TryGetValue(slot.InstanceId, out var c))
                SlideTo(c, tops[slot.InstanceId]);
        _canvas.Height = Math.Max(result.TotalHeight + (_gapIndex >= 0 ? _gapHeight + _model.Options.ItemSpacing : 0), 0);
        PositionPlaceholder(result, tops);
    }

    private void SlideTo(WidgetChrome chrome, double top)
    {
        var from = Canvas.GetTop(chrome);
        Canvas.SetTop(chrome, top);
        if (!AnimationsEnabled || !chrome.IsLoaded || double.IsNaN(from) || Math.Abs(from - top) < 0.5)
        {
            chrome.BeginAnimation(Canvas.TopProperty, null);
            return;
        }
        chrome.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(from, top, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    // ---- drag support ---------------------------------------------------------

    public void BeginDrag(string instanceId)
    {
        _draggingId = instanceId;
        if (_chromes.TryGetValue(instanceId, out var c))
        {
            Canvas.SetTop(c, Canvas.GetTop(c)); // freeze the current (possibly animated) position
            c.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetZIndex(c, 500);
        }
    }

    /// <summary>Moves the dragged chrome to follow the pointer (canvas coordinates).</summary>
    public void MoveDragged(string instanceId, double top)
    {
        if (_chromes.TryGetValue(instanceId, out var c)) Canvas.SetTop(c, top);
    }

    public void EndDrag()
    {
        if (_draggingId != null && _chromes.TryGetValue(_draggingId, out var c)) Canvas.SetZIndex(c, 0);
        _draggingId = null;
        _gapIndex = -1;
        _indicator.Visibility = Visibility.Collapsed;
        SetDetachHint(false);
        SetBlockedHint(false);
        RequestRelayout();
    }

    /// <summary>Converts a screen-space Y (same space as <see cref="Interop.CursorDips"/>) to canvas Y.</summary>
    public double ScreenYToCanvas(double screenY) =>
        screenY - Interop.ScreenBounds(this).Top + _scroll.VerticalOffset;

    /// <summary>Shown while a widget that cannot float is dragged outside the Host.</summary>
    public void SetBlockedHint(bool on)
    {
        _blockedBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) _frame.BorderBrush = HostBrushes.Pinned;
        else if (ReferenceEquals(_frame.BorderBrush, HostBrushes.Pinned)) _frame.BorderBrush = Brushes.Transparent;
    }

    internal UIElement GapMarker => _indicator;

    internal bool BlockedHintVisible => _blockedBadge.Visibility == Visibility.Visible;

    public void SetDetachHint(bool on) =>
        _frame.BorderBrush = on ? HostBrushes.Detach : Brushes.Transparent;

    /// <summary>Highlights the panel as a valid dock target while a floating widget hovers it.</summary>
    public void SetDockHint(bool on) =>
        _frame.BorderBrush = on ? HostBrushes.Drop : Brushes.Transparent;

    private static bool IsOverChrome(object source) => HasAncestor<WidgetChrome>(source);

    private static bool IsOverScrollBar(object source) =>
        HasAncestor<System.Windows.Controls.Primitives.ScrollBar>(source);

    private static bool HasAncestor<T>(object source) where T : DependencyObject
    {
        var d = source as DependencyObject;
        while (d != null)
        {
            if (d is T) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
