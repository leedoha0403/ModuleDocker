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
        new Application().Run(window);
    }
}
