using System.Windows.Controls;
using SmartBOQ.App.ViewModels;

namespace SmartBOQ.App.Views.Compare;

/// <summary>
/// Interaction logic for ColumnChannelsTab.xaml
/// </summary>
public partial class ColumnChannelsTab : UserControl
{
    public MainViewModel? ViewModel => DataContext as MainViewModel;

    public ColumnChannelsTab()
    {
        InitializeComponent();
    }
}
