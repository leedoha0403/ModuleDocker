using System.Windows;
using System.Windows.Controls;
using DiskUsage.Core;
using DiskUsage.Presentation;
using Dora.Widget.Abstractions;

namespace DiskUsage.App;

/// <summary>Standalone shell: the same ViewModel and views as the hosted widget, no Host involved.</summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Standalone: no Host publishes the ModuleDock.* theme, so provide the same (dark) defaults.
        var app = new Application();
        foreach (var (key, hex) in new[] { ("Text", "#F4F7F5"), ("Muted", "#A9B3BD"), ("Accent", "#3FDDB2"), ("Border", "#2A3336") })
            app.Resources["ModuleDock.Brush." + key] = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

        var vm = new DiskViewModel(new SystemDiskService(), TimeSpan.FromSeconds(30));
        var summary = new DiskSummaryView(vm);
        summary.OnDisplayModeChanged(WidgetDisplayMode.Natural);

        var panel = new StackPanel();
        panel.Children.Add(summary);
        panel.Children.Add(new DiskDetailView(vm));

        var window = new Window
        {
            Title = "Disk Usage",
            Width = 480,
            Height = 520,
            Content = panel,
            Background = System.Windows.Media.Brushes.Black
        };
        app.Run(window);
    }
}
