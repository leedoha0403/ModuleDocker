using Dora.Widget.SDK;

namespace DiskUsage.Presentation;

public partial class DiskSummaryView : ModeTemplateView
{
    public DiskSummaryView(DiskViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Content = viewModel;
        OnDisplayModeChanged(viewModel.DisplayMode);
    }
}
