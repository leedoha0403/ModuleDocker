using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dora.Widget.Runtime.HostWindow;

namespace Dora.Widget.Host.HostWindow;

/// <summary>Edits window (snap / auto-hide) and widget-interaction preferences; the state machine sanitizes the result.</summary>
public sealed class HostSettingsWindow : Window
{
    private readonly HostWindowSettings _original;
    private readonly HostInteractionSettings _originalInteraction;
    private readonly CheckBox _onTop = Check("항상 다른 창 위에 표시");
    private readonly CheckBox _snap = Check("화면 가장자리에 자석처럼 붙이기");
    private readonly Dictionary<SnapEdges, CheckBox> _edges = new();
    private readonly TextBox _distance = Box();
    private readonly CheckBox _autoHide = Check("붙어 있을 때 자동으로 숨기기");
    private readonly TextBox _hideDelay = Box();
    private readonly TextBox _revealDelay = Box();
    private readonly TextBox _thickness = Box();
    private readonly TextBox _hover = Box();
    private readonly TextBox _release = Box();
    private readonly TextBox _dragThreshold = Box();
    private readonly TextBox _spacing = Box();

    public HostSettingsWindow(HostWindowSettings current, HostInteractionSettings interaction)
    {
        _original = current;
        _originalInteraction = interaction;
        Title = "창 설정";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = HostBrushes.Background;
        Foreground = HostBrushes.Text;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic, Segoe UI Symbol");
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(18), MinWidth = 320 };
        panel.Children.Add(_onTop);

        panel.Children.Add(Section("화면 가장자리"));
        panel.Children.Add(_snap);
        var edges = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 4, 0, 4) };
        foreach (var (flag, name) in new[]
                 { (SnapEdges.Left, "왼쪽"), (SnapEdges.Right, "오른쪽"), (SnapEdges.Top, "위"), (SnapEdges.Bottom, "아래") })
        {
            var cb = Check(name);
            cb.Margin = new Thickness(0, 0, 12, 0);
            _edges[flag] = cb;
            edges.Children.Add(cb);
        }
        panel.Children.Add(edges);
        panel.Children.Add(Row("붙는 거리 (DIP)", _distance));

        panel.Children.Add(Section("자동 숨김"));
        panel.Children.Add(_autoHide);
        panel.Children.Add(Row("숨기기까지 대기 (ms)", _hideDelay));
        panel.Children.Add(Row("나타나기까지 대기 (ms)", _revealDelay));
        panel.Children.Add(Row("핸들 두께 (DIP)", _thickness));

        panel.Children.Add(Section("위젯 동작"));
        panel.Children.Add(Row("마우스 올림 확대 지연 (ms)", _hover));
        panel.Children.Add(Row("포커스 해제 지연 (ms)", _release));
        panel.Children.Add(Row("드래그 시작 거리 (DIP)", _dragThreshold));
        panel.Children.Add(Row("위젯 간격 (DIP)", _spacing));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = HostStyles.MakeButton("적용", "", null);
        ok.Padding = new Thickness(18, 3, 18, 3);
        ok.IsDefault = true;
        ok.Margin = new Thickness(0, 0, 8, 0);
        var cancel = HostStyles.MakeButton("취소", "", null);
        cancel.Padding = new Thickness(18, 3, 18, 3);
        cancel.IsCancel = true;
        ok.Click += (_, _) =>
        {
            Result = Read();
            InteractionResult = ReadInteraction();
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        Load(current, interaction);
    }

    public HostWindowSettings Result { get; private set; } = new();
    public HostInteractionSettings InteractionResult { get; private set; } = new();

    private static CheckBox Check(string text) =>
        new() { Content = text, Foreground = HostBrushes.Text, Margin = new Thickness(0, 2, 0, 2) };

    private static TextBox Box() => new()
    {
        Width = 74,
        HorizontalContentAlignment = HorizontalAlignment.Right,
        Background = HostBrushes.Surface,
        Foreground = HostBrushes.Text,
        BorderBrush = HostBrushes.Border,
        CaretBrush = HostBrushes.Text,
        Padding = new Thickness(4, 2, 4, 2)
    };

    private static FrameworkElement Section(string title) => new TextBlock
    {
        Text = title,
        FontWeight = FontWeights.Bold,
        Foreground = HostBrushes.Focus,
        Margin = new Thickness(0, 16, 0, 4)
    };

    private static FrameworkElement Row(string label, TextBox box)
    {
        var p = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(box, Dock.Right);
        p.Children.Add(box);
        p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = HostBrushes.Muted, Margin = new Thickness(0, 0, 12, 0) });
        return p;
    }

    private void Load(HostWindowSettings s, HostInteractionSettings i)
    {
        _onTop.IsChecked = s.AlwaysOnTop;
        _snap.IsChecked = s.SnapEnabled;
        foreach (var (flag, cb) in _edges) cb.IsChecked = (s.AllowedEdges & flag) != 0;
        _distance.Text = Fmt(s.SnapDistance);
        _autoHide.IsChecked = s.AutoHideEnabled;
        _hideDelay.Text = s.AutoHideDelayMs.ToString(CultureInfo.InvariantCulture);
        _revealDelay.Text = s.RevealDelayMs.ToString(CultureInfo.InvariantCulture);
        _thickness.Text = Fmt(s.HandleThickness);
        _hover.Text = i.HoverDelayMs.ToString(CultureInfo.InvariantCulture);
        _release.Text = i.FocusReleaseDelayMs.ToString(CultureInfo.InvariantCulture);
        _dragThreshold.Text = Fmt(i.DragThreshold);
        _spacing.Text = Fmt(i.ItemSpacing);
    }

    private HostWindowSettings Read()
    {
        var edges = SnapEdges.None;
        foreach (var (flag, cb) in _edges)
            if (cb.IsChecked == true) edges |= flag;

        return _original with
        {
            AlwaysOnTop = _onTop.IsChecked == true,
            SnapEnabled = _snap.IsChecked == true,
            AllowedEdges = edges,
            SnapDistance = Num(_distance.Text, _original.SnapDistance),
            AutoHideEnabled = _autoHide.IsChecked == true,
            AutoHideDelayMs = (int)Num(_hideDelay.Text, _original.AutoHideDelayMs),
            RevealDelayMs = (int)Num(_revealDelay.Text, _original.RevealDelayMs),
            HandleThickness = Num(_thickness.Text, _original.HandleThickness)
        };
    }

    private HostInteractionSettings ReadInteraction() => _originalInteraction with
    {
        HoverDelayMs = (int)Num(_hover.Text, _originalInteraction.HoverDelayMs),
        FocusReleaseDelayMs = (int)Num(_release.Text, _originalInteraction.FocusReleaseDelayMs),
        DragThreshold = Num(_dragThreshold.Text, _originalInteraction.DragThreshold),
        ItemSpacing = Num(_spacing.Text, _originalInteraction.ItemSpacing)
    };

    private static double Num(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
