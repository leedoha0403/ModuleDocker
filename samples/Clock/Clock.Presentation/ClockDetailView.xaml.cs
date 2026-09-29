using System.Windows.Controls;

namespace Clock.Presentation;

public partial class ClockDetailView : UserControl
{
    public ClockDetailView(ClockViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
