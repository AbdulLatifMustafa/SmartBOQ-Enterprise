using System.Windows.Controls;
using SmartBOQ.App.ViewModels;

namespace SmartBOQ.App.Views.Compare;

/// <summary>
/// Interaction logic for SheetLinkerTab.xaml
/// </summary>
public partial class SheetLinkerTab : UserControl
{
    public MainViewModel? ViewModel => DataContext as MainViewModel;

    public SheetLinkerTab()
    {
        InitializeComponent();
    }
}
