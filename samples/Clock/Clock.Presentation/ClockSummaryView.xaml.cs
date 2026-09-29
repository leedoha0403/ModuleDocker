using Dora.Widget.SDK;

namespace Clock.Presentation;

public partial class ClockSummaryView : ModeTemplateView
{
    public ClockSummaryView(ClockViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Content = viewModel;
        OnDisplayModeChanged(viewModel.DisplayMode);
    }
}
