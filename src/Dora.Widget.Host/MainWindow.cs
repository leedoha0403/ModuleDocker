using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Dora.Widget.Abstractions;

namespace Dora.Widget.Host;

/// <summary>The Host window: a slim toolbar over the docked widget area.</summary>
public sealed class MainWindow : Window
{
    private readonly HostController _controller;

    public MainWindow(HostController controller)
    {
        _controller = controller;
        Title = "ModuleDock";
        Width = 300;
        Height = 560;
        MinWidth = 160;
        MinHeight = 120;
        Background = HostBrushes.Background;
        Foreground = HostBrushes.Text;

        var root = new DockPanel();

        var bar = new DockPanel { Margin = new Thickness(6, 4, 6, 4), LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Top);

        var add = new Button { Content = "+", Width = 26, Height = 22, ToolTip = "Add widget", Focusable = false };
        add.Click += (_, _) => ShowAddMenu(add);
        DockPanel.SetDock(add, Dock.Right);

        var top = new ToggleButton { Content = "Top", Height = 22, Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(0, 0, 4, 0), ToolTip = "Keep on top", Focusable = false };
        top.Checked += (_, _) => Topmost = true;
        top.Unchecked += (_, _) => Topmost = false;
        DockPanel.SetDock(top, Dock.Right);

        bar.Children.Add(add);
        bar.Children.Add(top);
        bar.Children.Add(new TextBlock
        {
            Text = "ModuleDock",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = HostBrushes.Text,
            FontWeight = FontWeights.SemiBold
        });

        root.Children.Add(bar);
        root.Children.Add(controller.Panel);
        Content = root;

        controller.HostBoundsProvider = () =>
            WindowState == WindowState.Normal
                ? new WidgetRect(Left, Top, Width, Height)
                : new WidgetRect(RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height);

        LocationChanged += (_, _) => controller.ScheduleSave();
        SizeChanged += (_, _) => controller.ScheduleSave();
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

    private void ShowAddMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var manifests = _controller.Registry.Manifests.OrderBy(m => m.Name).ToList();
        if (manifests.Count == 0)
            menu.Items.Add(new MenuItem { Header = "(no widgets installed)", IsEnabled = false });

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
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "ModuleDock"); }
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
