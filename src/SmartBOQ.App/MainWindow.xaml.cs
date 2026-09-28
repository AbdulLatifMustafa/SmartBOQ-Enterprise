using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SmartBOQ.App.ViewModels;

namespace SmartBOQ.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (App.Services != null)
        {
            var vm = App.Services.GetService<MainViewModel>();
            if (vm != null)
            {
                DataContext = vm;
            }
        }
    }
}