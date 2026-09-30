using System.Windows.Controls;

namespace DiskUsage.Presentation;

public partial class DiskDetailView : UserControl
{
    public DiskDetailView(DiskViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
