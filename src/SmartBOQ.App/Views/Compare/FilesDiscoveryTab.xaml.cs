using System.Windows;
using System.Windows.Controls;
using SmartBOQ.App.ViewModels;

namespace SmartBOQ.App.Views.Compare;

/// <summary>
/// Interaction logic for FilesDiscoveryTab.xaml.
/// Implements isolated drag-and-drop file ingestion and sheet inspection.
/// </summary>
public partial class FilesDiscoveryTab : UserControl
{
    public MainViewModel? ViewModel => DataContext as MainViewModel;

    public FilesDiscoveryTab()
    {
        InitializeComponent();
    }

    private void FilesDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 && ViewModel != null)
            {
                _ = ViewModel.IngestFilesAsync(files);
            }
        }
    }

    private void FilesDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }
}
