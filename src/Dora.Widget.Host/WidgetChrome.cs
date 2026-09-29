using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Dora.Widget.Abstractions;
using Dora.Widget.Runtime;

namespace Dora.Widget.Host;

/// <summary>
/// Host-owned frame around a widget summary view. The same chrome instance (and therefore the same
/// summary view / ViewModel) is moved between the dock canvas and floating windows.
/// </summary>
public sealed class WidgetChrome : Border
{
    private readonly Grid _root = new();
    private readonly ContentPresenter _content = new();
    private readonly ToggleButton _pin;
    private readonly WidgetInstance _instance;

    public WidgetChrome(WidgetInstance instance, object summaryView)
    {
        _instance = instance;
        InstanceId = instance.State.InstanceId;

        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Background = HostBrushes.Surface;
        BorderBrush = HostBrushes.Border;
        SnapsToDevicePixels = true;
        ClipToBounds = true;

        _content.Content = summaryView;
        _root.Children.Add(_content);

        _pin = new ToggleButton
        {
            Content = "", // Segoe MDL2 Pin
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Cursor = Cursors.Arrow,
            Visibility = Visibility.Collapsed,
            ToolTip = "Pin",
            Focusable = false
        };
        // The pin button must not start a drag or count as a widget click.
        _pin.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
        _pin.PreviewMouseLeftButtonUp += (_, e) =>
        {
            // Button-down was handled, so Click never fires: raise the toggle here.
            e.Handled = true;
            PinToggled?.Invoke(this);
        };
        _root.Children.Add(_pin);

        Child = _root;
        Refresh();
    }

    public string InstanceId { get; }
    public WidgetInstance Instance => _instance;
    public object SummaryView => _content.Content;

    /// <summary>Raised when the pin button is used.</summary>
    public event Action<WidgetChrome>? PinToggled;

    /// <summary>Sets the Host-selected display mode and informs the view.</summary>
    public void ApplyMode(WidgetDisplayMode mode)
    {
        if (_content.Content is IDisplayModeAware a) a.OnDisplayModeChanged(mode);
        else if (_content.Content is FrameworkElement { DataContext: IDisplayModeAware vm }) vm.OnDisplayModeChanged(mode);
    }

    /// <summary>Re-evaluates visuals from the runtime state.</summary>
    public void Refresh()
    {
        var s = _instance.State;
        var pinned = s.InteractionState == WidgetInteractionState.Pinned;
        var focused = s.InteractionState is WidgetInteractionState.Focused or WidgetInteractionState.FocusReleasePending;
        var dragging = s.InteractionState == WidgetInteractionState.Dragging;

        BorderBrush = pinned ? HostBrushes.Pinned : focused ? HostBrushes.Focus : HostBrushes.Border;
        Opacity = dragging ? 0.85 : 1;
        _pin.IsChecked = pinned;
        _pin.ToolTip = pinned ? "Unpin" : "Pin";
        _pin.Visibility = pinned || focused || (s.IsPointerOver && !dragging)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Detaches the chrome from whatever container currently holds it.</summary>
    public void RemoveFromParent()
    {
        switch (Parent)
        {
            case Panel p: p.Children.Remove(this); break;
            case ContentControl c: c.Content = null; break;
            case Decorator d: d.Child = null; break;
        }
    }
}

internal static class HostBrushes
{
    public static readonly Brush Surface = Freeze(new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x2E)));
    public static readonly Brush Border = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4A)));
    public static readonly Brush Focus = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF)));
    public static readonly Brush Pinned = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xA9, 0x3B)));
    public static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x22)));
    public static readonly Brush Text = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEC)));
    public static readonly Brush Drop = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF)));
    public static readonly Brush Detach = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x5B, 0x5B)));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}
