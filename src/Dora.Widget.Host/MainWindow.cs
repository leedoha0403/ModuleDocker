using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Dora.Widget.Abstractions;
using Dora.Widget.Host.HostWindow;
using Dora.Widget.Host.Update;
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
    private readonly Button _update;
    private UpdateRelease? _availableUpdate;
    private bool _updating;
    private HostWindowController? _placement;
    private HostInteractionSettings _interaction = new();
    private Action<HostInteractionSettings>? _applyInteraction;

    public MainWindow(HostController controller)
    {
        _controller = controller;
        Title = "ModuleDock";
        Icon = HostIcon.Frame;
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
        _update = HostStyles.MakeButton("⬆", "", (_, _) => _ = InstallUpdateAsync());
        _update.Margin = new Thickness(0, 0, 6, 0);
        _update.Visibility = Visibility.Collapsed;
        var add = HostStyles.MakeButton("＋", "위젯 추가", (_, _) => ShowAddMenu());
        add.Margin = new Thickness(0, 0, 6, 0);
        var settings = HostStyles.MakeButton("⚙", "창 설정 (스냅 / 자동 숨김)", (_, _) => ShowSettings());
        settings.Margin = new Thickness(0, 0, 6, 0);
        var close = HostStyles.MakeButton("×", "종료", (_, _) => Close());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(_update);
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

    /// <summary>Rounded dark-teal square: a screen outline with a docked column of three cards (same as the app icon).</summary>
    private static FrameworkElement BuildIcon()
    {
        var canvas = new Canvas { Width = 30, Height = 30 };
        canvas.Children.Add(new Rectangle
        {
            Width = 8, Height = 18, RadiusX = 2, RadiusY = 2, Stroke = HostBrushes.Focus, StrokeThickness = 1.5,
            Margin = new Thickness(5, 6, 0, 0)
        });
        var opacities = new[] { 1.0, 0.66, 0.4 };
        for (var i = 0; i < 3; i++)
            canvas.Children.Add(new Rectangle
            {
                Width = 9, Height = 4.6, RadiusX = 1.5, RadiusY = 1.5, Fill = HostBrushes.Focus, Opacity = opacities[i],
                Margin = new Thickness(16, 6 + i * 6.7, 0, 0)
            });
        return new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = HostBrushes.IconBack,
            Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = canvas
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
            var dlg = new HostSettingsWindow(_placement.Machine.Settings, _interaction, () => CheckForUpdateAsync(manual: true)) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _placement.Machine.ApplySettings(dlg.Result);
            HostTheme.Apply(dlg.Result.ThemeMode);
            _interaction = dlg.InteractionResult.Sanitized();
            _applyInteraction?.Invoke(_interaction);
        }
    }

    /// <summary>
    /// Looks for a newer release. Shows the header update button when there is one; returns a one-line status
    /// for the settings window. A silent startup check never interrupts the user.
    /// </summary>
    public async Task<string> CheckForUpdateAsync(bool manual)
    {
        try
        {
            var release = await UpdateFlow.CheckAsync(CancellationToken.None);
            if (release == null) return "최신 버전입니다 (또는 확인할 수 없습니다).";
            _availableUpdate = release;
            _update.ToolTip = $"새 버전 {release.TagName} 설치 (현재 {UpdateFlow.DisplayVersion})";
            _update.Visibility = Visibility.Visible;
            return $"새 버전 {release.TagName} 이 있습니다. 헤더의 ⬆ 버튼으로 설치하세요.";
        }
        catch (Exception ex)
        {
            return manual ? "확인에 실패했습니다: " + ex.Message : "";
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is not { } release || _updating) return;
        MessageBoxResult answer;
        using (_controller.Popups.Scope())
            answer = MessageBox.Show(this, $"{release.TagName} 로 업데이트하고 다시 시작할까요?\n(현재 {UpdateFlow.DisplayVersion})",
                "ModuleDock", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _updating = true;
        var progress = new Progress<double>(p => _update.Content = $"{p:P0}");
        var outcome = await UpdateFlow.InstallAsync(release, progress, CancellationToken.None);
        _updating = false;
        _update.Content = "⬆";
        if (outcome.AppMustExit)
        {
            Application.Current.Shutdown();
            return;
        }
        using (_controller.Popups.Scope())
            MessageBox.Show(this, outcome.Message, "ModuleDock");
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
