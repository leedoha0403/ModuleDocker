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
    private readonly Button _pin;
    private readonly WidgetInstance _instance;

    public WidgetChrome(WidgetInstance instance, object summaryView)
    {
        _instance = instance;
        InstanceId = instance.State.InstanceId;

        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Background = HostBrushes.Surface;
        BorderBrush = HostBrushes.Border;
        SnapsToDevicePixels = true;
        ClipToBounds = true;

        _content.Content = summaryView;
        _root.Children.Add(_content);

        _pin = new Button
        {
            Content = "", // Segoe MDL2 Pin
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Width = 22,
            Height = 22,
            MinWidth = 22,
            MinHeight = 22,
            Padding = new Thickness(0),
            Style = HostStyles.Button,
            Background = HostBrushes.Background,
            BorderBrush = HostBrushes.Border,
            Foreground = HostBrushes.Text,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Cursor = Cursors.Arrow,
            Visibility = Visibility.Collapsed,
            ToolTip = "고정",
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
        _pin.Foreground = pinned ? HostBrushes.Pinned : HostBrushes.Text;
        Opacity = dragging ? 0.85 : 1;
        _pin.ToolTip = pinned ? "고정 해제" : "고정";
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
