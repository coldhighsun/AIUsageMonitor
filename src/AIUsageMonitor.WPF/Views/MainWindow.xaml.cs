using AIUsageMonitor.WPF.ViewModels;

namespace AIUsageMonitor.WPF.Views;

public partial class MainWindow
{
    public MainWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}