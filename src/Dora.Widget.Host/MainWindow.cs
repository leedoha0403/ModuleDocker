using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Dora.Widget.Abstractions;
using Dora.Widget.Host.HostWindow;
using Dora.Widget.Runtime.HostWindow;

namespace Dora.Widget.Host;

/// <summary>Host-level event topics widgets may publish.</summary>
public static class HostTopics
{
    /// <summary>Payload <c>true</c> lights the handle's attention dot while the Host is hidden; <c>false</c> clears it.</summary>
    public const string Attention = "host.attention";
}

/// <summary>
/// The Host window. Borderless rounded panel in the style of the AIUsage Monitor widget: icon and title on
/// the left, fold / add / settings / close buttons on the right, the docked widgets below. It is dragged by
/// its header and snaps to screen edges (see <see cref="HostWindowController"/>).
/// </summary>
public sealed class MainWindow : Window
{
    public const double BaseMinWidth = 280;

    private readonly HostController _controller;
    private readonly TextBlock _subtitle;
    private readonly Button _fold;
    private HostWindowController? _placement;
    private HostInteractionSettings _interaction = new();
    private Action<HostInteractionSettings>? _applyInteraction;

    public MainWindow(HostController controller)
    {
        _controller = controller;
        Title = "ModuleDock";
        Width = 330;
        Height = 560;
        MinWidth = BaseMinWidth;
        MinHeight = 180;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Foreground = HostBrushes.Text;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic, Segoe UI Symbol");

        // ---- header: icon + title/subtitle on the left, buttons on the right
        _subtitle = new TextBlock { Foreground = HostBrushes.Muted, FontSize = 12 };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "ModuleDock", FontWeight = FontWeights.Bold, FontSize = 15, Foreground = HostBrushes.Text });
        titles.Children.Add(_subtitle);

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(BuildIcon());
        left.Children.Add(titles);

        _fold = HostStyles.MakeButton("", "", (_, _) => _placement?.ToggleFold());
        _fold.Margin = new Thickness(0, 0, 6, 0);
        _fold.Visibility = Visibility.Collapsed;
        var add = HostStyles.MakeButton("＋", "위젯 추가", (_, _) => ShowAddMenu());
        add.Margin = new Thickness(0, 0, 6, 0);
        var settings = HostStyles.MakeButton("⚙", "창 설정 (스냅 / 자동 숨김)", (_, _) => ShowSettings());
        settings.Margin = new Thickness(0, 0, 6, 0);
        var close = HostStyles.MakeButton("×", "종료", (_, _) => Close());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(_fold);
        buttons.Children.Add(add);
        buttons.Children.Add(settings);
        buttons.Children.Add(close);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12), Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(left);
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);
        // The window has no title bar: the header is the drag handle (the OS move loop keeps edge snapping working).
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { /* button released before the loop started */ }
        };

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(header);
        Grid.SetRow(controller.Panel, 1);
        body.Children.Add(controller.Panel);

        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = HostBrushes.Background,
            BorderBrush = HostBrushes.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = body
        };

        controller.HostBoundsProvider = () =>
            WindowState == WindowState.Normal
                ? new WidgetRect(Left, Top, Width, Height)
                : new WidgetRect(RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height);

        controller.Panel.Relaid += () =>
        {
            UpdateMinWidth();
            UpdateSubtitle();
        };
        LocationChanged += (_, _) => controller.ScheduleSave();
        SizeChanged += (_, _) => controller.ScheduleSave();
        UpdateSubtitle();
    }

    /// <summary>Rounded dark-teal square with three rising bars.</summary>
    private static FrameworkElement BuildIcon()
    {
        var bars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var h in new double[] { 7, 12, 17 })
            bars.Children.Add(new Rectangle
            {
                Width = 4, Height = h, RadiusX = 1.5, RadiusY = 1.5, Fill = HostBrushes.Focus,
                Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Bottom
            });
        return new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = HostBrushes.IconBack,
            Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = bars
        };
    }

    private void UpdateSubtitle()
    {
        var n = _controller.Runtime.Instances.Count;
        _subtitle.Text = n == 0 ? "위젯 없음" : $"위젯 {n}개";
    }

    /// <summary>The Host must never be narrower than what its widgets declare they can render in.</summary>
    private void UpdateMinWidth()
    {
        var windowExtra = Math.Max(0, ActualWidth - _controller.Panel.ActualWidth);
        var need = _controller.Model.RequiredMinWidth + WidgetHostPanel.WidthOverhead + windowExtra;
        var min = Math.Max(BaseMinWidth, need);
        if (Math.Abs(MinWidth - min) > 0.5) MinWidth = min;
    }

    /// <summary>Connects edge snap / auto-hide; without it the window behaves like a plain window.</summary>
    public void AttachPlacement(HostWindowController placement)
    {
        _placement = placement;
        _controller.Popups.Changed += placement.SetPopupOpen;
        _controller.DragActivityChanged += placement.SetWidgetDragActivity;

        // Widgets can ask for attention with the event "host.attention" (payload: true/false). While the Host
        // is hidden this lights up the handle; revealing the Host clears it.
        _controller.Runtime.Events.Subscribe(HostTopics.Attention, payload =>
            Dispatcher.BeginInvoke(new Action(() => placement.Handle.Indicator = payload is true)));
        placement.Machine.StateChanged += (_, now) =>
        {
            if (now == HostWindowState.SnappedVisible) placement.Handle.Indicator = false;
            UpdateFoldButton();
        };
        placement.Machine.SettingsChanged += UpdateFoldButton;
        UpdateFoldButton();
    }

    /// <summary>Supplies the current interaction tuning and how to apply/persist a change to it.</summary>
    public void AttachInteraction(HostInteractionSettings current, Action<HostInteractionSettings> apply)
    {
        _interaction = current;
        _applyInteraction = apply;
    }

    /// <summary>
    /// Fold arrow like the AIUsage Monitor widget: only while snapped; it points toward the edge to fold
    /// (auto-hide on) and away from it to unfold (auto-hide off).
    /// </summary>
    private void UpdateFoldButton()
    {
        var m = _placement?.Machine;
        if (m is null || m.Placement != HostPlacementState.Snapped)
        {
            _fold.Visibility = Visibility.Collapsed;
            return;
        }
        var folded = m.Settings.AutoHideEnabled;
        var toward = m.SnappedEdge switch { ScreenEdge.Left => "◀", ScreenEdge.Right => "▶", ScreenEdge.Top => "▲", _ => "▼" };
        var away = m.SnappedEdge switch { ScreenEdge.Left => "▶", ScreenEdge.Right => "◀", ScreenEdge.Top => "▼", _ => "▲" };
        _fold.Content = folded ? away : toward;
        _fold.ToolTip = folded ? "자동 숨김 끄기 (펼쳐 두기)" : "가장자리로 접기";
        _fold.Visibility = Visibility.Visible;
    }

    /// <summary>Applies saved bounds only when they are at least partly visible on the virtual screen.</summary>
    public void ApplyBounds(WidgetRect b)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(b.X, b.Y, Math.Max(b.Width, MinWidth), Math.Max(b.Height, MinHeight));
        var visible = Rect.Intersect(screen, r);
        if (visible.IsEmpty || visible.Width < 60 || visible.Height < 40) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = r.X; Top = r.Y; Width = r.Width; Height = r.Height;
    }

    private void ShowSettings()
    {
        if (_placement == null) return;
        using (_controller.Popups.Scope())
        {
            var dlg = new HostSettingsWindow(_placement.Machine.Settings, _interaction) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _placement.Machine.ApplySettings(dlg.Result);
            _interaction = dlg.InteractionResult.Sanitized();
            _applyInteraction?.Invoke(_interaction);
        }
    }

    private void ShowAddMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        _controller.Popups.Track(menu);
        var manifests = _controller.Registry.Manifests.OrderBy(m => m.Name).ToList();
        if (manifests.Count == 0)
            menu.Items.Add(new MenuItem { Header = "(설치된 위젯 없음)", IsEnabled = false });

        foreach (var m in manifests)
        {
            var existing = _controller.Runtime.Instances.Any(i => i.State.WidgetId == m.Id);
            var item = new MenuItem
            {
                Header = m.Name,
                IsEnabled = m.AllowMultipleInstances || !existing,
                ToolTip = m.Description
            };
            var id = m.Id;
            item.Click += async (_, _) =>
            {
                try { await _controller.AddWidgetAsync(id); }
                catch (Exception ex)
                {
                    using (_controller.Popups.Scope())
                        MessageBox.Show(this, ex.Message, "ModuleDock");
                }
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
