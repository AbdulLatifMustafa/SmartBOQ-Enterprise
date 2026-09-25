using System.Windows;
using System.Windows.Controls;

namespace SmartBOQ.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void NavTab_Summary_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 0;
    }

    private void NavTab_Compare_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
    }

    private void NavTab_Pricing_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 2;
    }

    private void NavTab_Rates_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 3;
    }

    private void NavTab_Export_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 4;
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;
        switch (MainTabs.SelectedIndex)
        {
            case 0: if (TabBtn0 != null) TabBtn0.IsChecked = true; break;
            case 1: if (TabBtn1 != null) TabBtn1.IsChecked = true; break;
            case 2: if (TabBtn2 != null) TabBtn2.IsChecked = true; break;
            case 3: if (TabBtn3 != null) TabBtn3.IsChecked = true; break;
            case 4: if (TabBtn4 != null) TabBtn4.IsChecked = true; break;
        }
    }
}