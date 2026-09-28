using System.Windows.Controls;
using SmartBOQ.App.ViewModels;

namespace SmartBOQ.App.Views.Compare;

/// <summary>
/// Interaction logic for PipelineFlowTab.xaml (Visual workflow and reconciliation map).
/// </summary>
public partial class PipelineFlowTab : UserControl
{
    public MainViewModel? ViewModel => DataContext as MainViewModel;

    public PipelineFlowTab()
    {
        InitializeComponent();
    }
}
