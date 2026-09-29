using System.Windows;
using System.Windows.Controls;
using Clock.Core;
using Clock.Presentation;
using Dora.Widget.Abstractions;

namespace Clock.App;

/// <summary>Standalone shell: reuses the exact same ViewModel and views as the hosted widget.</summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        var vm = new ClockViewModel(new SystemClockService());
        var summary = new ClockSummaryView(vm);
        summary.OnDisplayModeChanged(WidgetDisplayMode.Natural);

        var panel = new StackPanel();
        panel.Children.Add(summary);
        panel.Children.Add(new ClockDetailView(vm));

        var window = new Window
        {
            Title = "Clock",
            Width = 420,
            Height = 420,
            Content = panel,
            Background = System.Windows.Media.Brushes.Black
        };
        new Application().Run(window);
    }
}
